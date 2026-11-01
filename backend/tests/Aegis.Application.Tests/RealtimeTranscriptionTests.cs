using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Aegis.Api.Controllers;
using Aegis.Api.Nodes;
using Aegis.Api.Nodes.Transport;
using Aegis.Application.Nodes;
using Aegis.Infrastructure.Nodes;
using Aegis.Infrastructure.Persistence;
using Aegis.Infrastructure.Voice.Transcription;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Aegis.Application.Tests;

public sealed class RealtimeTranscriptionTests
{
    private static Task Send(WebSocket socket, object value) => ElevenLabsRealtimeTranscription.SendJson(socket, value, default);
    private static async Task<JsonElement> Read(WebSocket socket)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var buffer = new byte[100000];
        var part = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token);
        Assert.True(part.EndOfMessage);
        Assert.Equal(WebSocketMessageType.Text, part.MessageType);
        return JsonDocument.Parse(buffer.AsSpan(0, part.Count).ToArray()).RootElement.Clone();
    }
    private sealed class Connector : IRealtimeTranscriptionConnector
    {
        public required TestServer Upstream;
        public int Connections;
        public Uri? Uri;
        public string? Key;
        public async Task<WebSocket> ConnectAsync(Uri uri, string apiKey, CancellationToken cancellationToken)
        {
            Uri = uri; Key = apiKey; Interlocked.Increment(ref Connections);
            return await Upstream.CreateWebSocketClient().ConnectAsync(new Uri("ws://localhost/"), cancellationToken);
        }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        public required WebApplication App;
        public required WebApplication Provider;
        public required Connector Connector;
        public required NodeRegistry Nodes;
        public required string Credential;
        public required Guid NodeId;
        public async Task<WebSocket> Connect(Guid? id = null)
        {
            var client = App.GetTestServer().CreateWebSocketClient();
            client.ConfigureRequest = r => r.Headers.Authorization = "AegisNode " + Credential;
            return await client.ConnectAsync(new Uri("ws://localhost/api/voice/transcriptions/realtime?transcriptionRequestId=" + (id ?? Guid.NewGuid())), default);
        }
        public async ValueTask DisposeAsync() { await App.DisposeAsync(); await Provider.DisposeAsync(); }
    }
    private static async Task<Fixture> Host(Func<WebSocket, Task> provider, bool remoteVad = false)
    {
        var upstream = WebApplication.CreateBuilder(); upstream.WebHost.UseTestServer();
        var providerApp = upstream.Build(); providerApp.UseWebSockets();
        providerApp.Run(async context => {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            try {
                await Send(socket, new { message_type = "session_started", session_id = "synthetic", config = new { sample_rate = 16000, vad_commit_strategy = remoteVad, model_id = "scribe_v2_realtime" } });
                await provider(socket);
            } catch (Exception error) when (error is WebSocketException or OperationCanceledException) { }
        });
        await providerApp.StartAsync();
        var connector = new Connector { Upstream = providerApp.GetTestServer() };
        var name = Guid.NewGuid().ToString();
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseTestServer();
        builder.Services.AddControllers().AddApplicationPart(typeof(RealtimeTranscriptionController).Assembly);
        builder.Services.AddNodeApi(); builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddDbContext<AegisDbContext>(o => o.UseInMemoryDatabase(name));
        builder.Services.AddScoped<INodeRegistry, NodeRegistry>(); builder.Services.AddScoped<INodeTransportHistory, NodeTransportHistory>();
        builder.Services.AddSingleton<IRealtimeTranscriptionConnector>(connector);
        builder.Services.AddSingleton(Options.Create(new ElevenLabsSttOptions { ApiKey = "provider-secret", BaseUrl = "https://api.elevenlabs.io" }));
        builder.Services.AddSingleton(Options.Create(new SttOptions()));
        builder.Services.AddScoped<ElevenLabsRealtimeTranscription>();
        var app = builder.Build(); app.UseWebSockets(); app.UseAuthentication(); app.UseAuthorization(); app.UseRateLimiter(); app.MapControllers(); await app.StartAsync();
        var db = new AegisDbContext(new DbContextOptionsBuilder<AegisDbContext>().UseInMemoryDatabase(name).Options);
        var nodes = new NodeRegistry(db, TimeProvider.System, app.Services.GetRequiredService<NodeConnectionRegistry>());
        var request = NodeIdentityTests.Request((await nodes.CreateCodeAsync(null)).Code, "android");
        var receipt = await nodes.PairAsync(request);
        var node = await nodes.FinalizeAsync(new(request.AttemptId, receipt.Credential));
        return new() { App = app, Provider = providerApp, Connector = connector, Nodes = nodes, Credential = receipt.Credential, NodeId = node.Id };
    }
    private static Task Audio(WebSocket socket, int bytes = 64000) => socket.SendAsync(new ArraySegment<byte>(new byte[bytes]), WebSocketMessageType.Binary, true, default);

    [Fact]
    public async Task RemoteVadIsRejectedInsteadOfAddingASecondEndpoint()
    {
        await using var fixture = await Host(async socket => {
            var close = new byte[128]; await socket.ReceiveAsync(close, default);
        }, remoteVad: true);
        using var client = await fixture.Connect();
        var error = await Read(client);
        Assert.Equal("error", error.GetProperty("type").GetString());
        Assert.Equal("provider_configuration_mismatch", error.GetProperty("code").GetString());
    }

    [Fact]
    public async Task NativeAuthenticationIsRequiredBeforeUpgradeAndProviderConnection()
    {
        await using var fixture = await Host(_ => Task.CompletedTask);
        var response = await fixture.App.GetTestClient().GetAsync("/api/voice/transcriptions/realtime?transcriptionRequestId=" + Guid.NewGuid());
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, fixture.Connector.Connections);
        using var http = fixture.App.GetTestClient(); http.DefaultRequestHeaders.Add("Authorization", "AegisNode " + fixture.Credential);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.GetAsync("/api/voice/transcriptions/realtime")).StatusCode);
    }

    [Fact]
    public async Task ManualLocalCommitAggregatesConfirmedSegmentsAndIgnoresDuplicateRepresentation()
    {
        var earlier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = await Host(async socket => {
            var audio = await Read(socket);
            Assert.Equal("input_audio_chunk", audio.GetProperty("message_type").GetString());
            Assert.False(audio.GetProperty("commit").GetBoolean());
            Assert.Equal(64000, Convert.FromBase64String(audio.GetProperty("audio_base_64").GetString()!).Length);
            await Send(socket, new { message_type = "partial_transcript", text = "não entregar às oito" });
            await Send(socket, new { message_type = "committed_transcript", text = "Não, às oito não." });
            await Send(socket, new { message_type = "committed_transcript_with_timestamps", text = "Não, às oito não." });
            earlier.SetResult();
            var commit = await Read(socket);
            Assert.True(commit.GetProperty("commit").GetBoolean());
            Assert.Equal("", commit.GetProperty("audio_base_64").GetString());
            await Send(socket, new { message_type = "committed_transcript", text = "Às nove e quinze para Júlia." });
            var close = new byte[128]; await socket.ReceiveAsync(close, default);
        });
        var id = Guid.NewGuid(); using var client = await fixture.Connect(id);
        var ready = await Read(client);
        Assert.Equal(id, ready.GetProperty("transcriptionRequestId").GetGuid());
        Assert.Equal(0, ready.GetProperty("minAudioMilliseconds").GetInt32());
        Assert.Equal("scribe_v2_realtime", ready.GetProperty("model").GetString());
        await Audio(client); await earlier.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // A confirmed provider segment is not a local turn boundary.
        var finalTask = Read(client);
        await Task.Delay(30); Assert.False(finalTask.IsCompleted);
        await Send(client, new { type = "commit" });
        var final = await finalTask;
        Assert.Equal("final", final.GetProperty("type").GetString());
        Assert.Equal(id, final.GetProperty("transcriptionRequestId").GetGuid());
        Assert.Equal("Não, às oito não. Às nove e quinze para Júlia.", final.GetProperty("text").GetString());
        Assert.DoesNotContain("provider-secret", final.ToString());
        Assert.Equal("provider-secret", fixture.Connector.Key);
        Assert.Contains("commit_strategy=manual", fixture.Connector.Uri!.Query);
        Assert.Contains("no_verbatim=false", fixture.Connector.Uri.Query);
        Assert.Contains("language_code=pt", fixture.Connector.Uri.Query);
        Assert.DoesNotContain("provider-secret", fixture.Connector.Uri.ToString());
    }

    [Theory]
    [InlineData(0, "invalid_audio")]
    [InlineData(64001, "frame_limit_exceeded")]
    public async Task InvalidOrOversizedAudioFallsBackWithoutPaddingOrProviderCommit(int bytes, string code)
    {
        var commits = 0;
        await using var fixture = await Host(async socket => {
            while (true) {
                var message = await Read(socket);
                if (message.TryGetProperty("commit", out var commit) && commit.GetBoolean()) Interlocked.Increment(ref commits);
            }
        });
        using var client = await fixture.Connect(); await Read(client);
        await Audio(client, bytes);
        var error = await Read(client);
        Assert.Equal("error", error.GetProperty("type").GetString());
        Assert.Equal(code, error.GetProperty("code").GetString());
        Assert.Equal(0, commits);
    }

    [Fact]
    public async Task ShortCommandCommitsTheActualAudioWithoutPaddingOrWait()
    {
        var size = 0;
        await using var fixture = await Host(async socket => {
            var audio = await Read(socket);
            size = Convert.FromBase64String(audio.GetProperty("audio_base_64").GetString()!).Length;
            Assert.False(audio.GetProperty("commit").GetBoolean());
            var commit = await Read(socket);
            Assert.True(commit.GetProperty("commit").GetBoolean());
            Assert.Equal("", commit.GetProperty("audio_base_64").GetString());
            await Send(socket, new { message_type = "committed_transcript", text = "Sim." });
            var close = new byte[128]; await socket.ReceiveAsync(close, default);
        });
        using var client = await fixture.Connect(); await Read(client);
        await Audio(client, 16000); await Send(client, new { type = "commit" });
        Assert.Equal("Sim.", (await Read(client)).GetProperty("text").GetString());
        Assert.Equal(16000, size);
    }

    [Fact]
    public async Task ClientCancellationReleasesPaidSessionAndAllowsNewAttempt()
    {
        await using var fixture = await Host(async socket => {
            var close = new byte[128]; await socket.ReceiveAsync(close, default);
        });
        using (var first = await fixture.Connect()) {
            await Read(first);
            await first.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "cancelled", default);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var closed = await first.ReceiveAsync(new ArraySegment<byte>(new byte[256]), deadline.Token);
            Assert.Equal(WebSocketMessageType.Close, closed.MessageType);
            // Observing the close is enough: no retry/backoff or artificial settling delay.
        }
        using var second = await fixture.Connect();
        Assert.Equal("ready", (await Read(second)).GetProperty("type").GetString());
        Assert.Equal(2, fixture.Connector.Connections);
        await second.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "cancelled", default);
    }

    [Fact]
    public async Task ProviderFailureIsSanitizedAndCannotProduceFinal()
    {
        await using var fixture = await Host(async socket => {
            await Read(socket);
            await Send(socket, new { message_type = "quota_exceeded", error = "sensitive provider-secret transcript" });
            var close = new byte[128]; await socket.ReceiveAsync(close, default);
        });
        using var client = await fixture.Connect(); await Read(client); await Audio(client);
        var error = await Read(client);
        Assert.Equal("error", error.GetProperty("type").GetString());
        Assert.Equal("provider_transcription_failed", error.GetProperty("code").GetString());
        Assert.DoesNotContain("sensitive", error.ToString());
    }

    [Fact]
    public async Task RevokedIdentityCannotCommitOrReceiveTranscript()
    {
        await using var fixture = await Host(async socket => {
            await Read(socket); var next = new byte[1024]; await socket.ReceiveAsync(next, default);
        });
        using var client = await fixture.Connect(); await Read(client); await Audio(client);
        await fixture.Nodes.RevokeAsync(fixture.NodeId, fixture.NodeId);
        await Send(client, new { type = "commit" });
        Assert.Equal("node_identity_changed", (await Read(client)).GetProperty("code").GetString());
    }

    [Fact]
    public void CapacityIsBoundedAndOldCleanupCannotReleaseAnotherIdentity()
    {
        var capacity = new RealtimeTranscriptionCapacity(); var node = Guid.NewGuid();
        var one = capacity.TryAcquire(node)!; Assert.Null(capacity.TryAcquire(node));
        using var two = capacity.TryAcquire(Guid.NewGuid()); using var three = capacity.TryAcquire(Guid.NewGuid()); using var four = capacity.TryAcquire(Guid.NewGuid());
        Assert.Null(capacity.TryAcquire(Guid.NewGuid())); one.Dispose();
        using var again = capacity.TryAcquire(node); one.Dispose(); Assert.Null(capacity.TryAcquire(node));
    }
}
