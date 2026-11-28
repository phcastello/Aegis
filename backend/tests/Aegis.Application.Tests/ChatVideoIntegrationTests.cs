using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aegis.Api.Controllers;
using Aegis.Application.Chat;
using Aegis.Application.Common;
using Aegis.Application.Llm;
using Aegis.Application.Models;
using Aegis.Application.Observability;
using Aegis.Application.Prompts;
using Aegis.Application.Runtime;
using Aegis.Application.Tools;
using Aegis.Application.Turns;
using Aegis.Application.Voice;
using Aegis.Application.Voice.Transcription;
using Aegis.Infrastructure.Chat;
using Aegis.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aegis.Application.Tests;

public sealed class ChatVideoIntegrationTests
{
    private const string VideoName = "amostra.mp4";
    private const string PrivateMarker = "private-video-provider-response";
    private const string DelegatedQuestion = "Volte a amostra.mp4 e examine o trecho em segundo plano.";

    private sealed class VideoOperationFactAttribute : FactAttribute
    {
        public VideoOperationFactAttribute()
        {
            if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/ffmpeg") ||
                !File.Exists("/usr/bin/ffprobe") || !File.Exists("/usr/bin/prlimit") ||
                string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AEGIS_OPERATION_TEST_DATABASE")))
                Skip = "Requires Linux FFmpeg and the disposable AEGIS_OPERATION_TEST_DATABASE.";
        }
    }

    [VideoOperationFact]
    public Task UploadedVideoCanBeInspectedRevisitedPlayedAndDelegatedPrivately() =>
        PrivateOperationPostgresTests.WithSchema(async create =>
        {
            await using var db = create();
            await db.Database.MigrateAsync();
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            var services = builder.Services;
            services.AddControllers().AddApplicationPart(typeof(ChatController).Assembly);
            services.AddScoped<AegisDbContext>(_ => create());
            services.AddScoped<IAegisDbContext>(sp => sp.GetRequiredService<AegisDbContext>());
            services.AddScoped<ChatUploadStore>();
            services.AddScoped<ResearchJobStore>();
            services.AddScoped<IResearchDelegationQueue>(sp => sp.GetRequiredService<ResearchJobStore>());
            services.AddScoped<IOwnerInstructions, OwnerInstructionsStore>();
            services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
            services.AddSingleton(TimeProvider.System);
            services.AddSingleton(new WebSearchOptions { Enabled = false });
            services.AddSingleton<IVideoAttachmentAudio, UnexpectedVideoSpeech>();
            services.AddScoped<AttachmentVideoTool>();
            services.AddScoped<IAegisTool>(sp => sp.GetRequiredService<AttachmentVideoTool>());
            services.AddScoped<IAegisToolRegistry, AegisToolRegistry>();
            services.AddScoped<IAegisToolLoop, AegisToolLoop>();
            services.AddSingleton<IAegisModelClient, VideoModel>();
            services.AddSingleton<IConversationTitleJobQueue, ConversationTitleJobQueue>();
            services.AddSingleton<IRuntimeContextProvider, RuntimeContextProvider>();
            services.AddSingleton<AegisMetrics>();
            services.AddSingleton<IActiveTurnRegistry, ActiveTurnRegistry>();
            services.AddScoped<IVoiceService>(sp => new VoiceService(sp.GetRequiredService<IAegisDbContext>(),
                sp.GetRequiredService<IActiveTurnRegistry>(), null!, null!));
            services.AddScoped<IPromptBuilder, PromptBuilder>();
            services.AddScoped<IChatService, ChatService>();
            await using var app = builder.Build();
            app.MapControllers();
            await app.StartAsync();
            using var client = app.GetTestClient();

            var video = await CreateVideoAsync();
            var videoUpload = await Upload(client, VideoName, "application/octet-stream", video);
            var noteUpload = await Upload(client, "nota.txt", "text/plain", "nota privada"u8.ToArray());
            var first = await Ask(client, new()
                { Content = "O que aparece no vídeo?", AttachmentUploadIds = [videoUpload, noteUpload] });
            var history = await History(client, first.ConversationId);
            var original = Assert.Single(history.Messages, message => message.Role == "user");
            var videoFile = Assert.Single(original.Attachments!, file => file.FileName == VideoName);
            var noteFile = Assert.Single(original.Attachments!, file => file.FileName == "nota.txt");
            var originalCitation = $"aegis-video:{original.Id:D}/{videoFile.Id:D}";
            Assert.Contains(originalCitation + "#t=0", first.Message.Content);

            var second = await Ask(client, new()
                { ConversationId = first.ConversationId, Content = "E entre 0,75 e 1,75 segundos?" });
            Assert.Contains(originalCitation + "#t=1", second.Message.Content);

            string AttachmentPath(Guid message, Guid file) => $"/api/chat/messages/{message}/attachments/{file}";
            var path = AttachmentPath(original.Id, videoFile.Id);
            using var download = await client.GetAsync(path);
            download.EnsureSuccessStatusCode();
            Assert.Equal("attachment", download.Content.Headers.ContentDisposition?.DispositionType);
            Assert.Equal(VideoName, download.Content.Headers.ContentDisposition?.FileNameStar);
            Assert.Equal(video, await download.Content.ReadAsByteArrayAsync());

            using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, path + "?inline=true");
            rangeRequest.Headers.Range = new RangeHeaderValue(4, 31);
            using var ranged = await client.SendAsync(rangeRequest);
            Assert.Equal(HttpStatusCode.PartialContent, ranged.StatusCode);
            Assert.Equal("video/mp4", ranged.Content.Headers.ContentType?.MediaType);
            Assert.Equal("bytes 4-31/" + video.Length, ranged.Content.Headers.ContentRange?.ToString());
            Assert.Null(ranged.Content.Headers.ContentDisposition);
            Assert.Equal(video[4..32], await ranged.Content.ReadAsByteArrayAsync());
            using var wrongMessage = await client.GetAsync(AttachmentPath(second.Message.Id, videoFile.Id) + "?inline=true");
            Assert.Equal(HttpStatusCode.NotFound, wrongMessage.StatusCode);
            using var notVideo = await client.GetAsync(AttachmentPath(original.Id, noteFile.Id) + "?inline=true");
            Assert.Equal(HttpStatusCode.NotFound, notVideo.StatusCode);

            var delegated = await Ask(client, new() { ConversationId = first.ConversationId, Content = DelegatedQuestion });
            Assert.Equal("Vou examinar esse trecho do vídeo.", delegated.Message.Content);
            var pending = Assert.Single(await client.GetFromJsonAsync<ResearchJobSnapshot[]>(
                $"/api/chat/research-jobs?conversationId={first.ConversationId}") ?? []);
            Assert.Equal("queued", pending.State);
            Assert.Equal("video", pending.Kind);

            using var worker = new ResearchWorker(app.Services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System,
                app.Services.GetRequiredService<WebSearchOptions>(), NullLogger<ResearchWorker>.Instance);
            Assert.True(await worker.ProcessNextAsync(default));
            var finished = await client.GetFromJsonAsync<ResearchJobSnapshot>($"/api/chat/research-jobs/{pending.Id}");
            Assert.NotNull(finished);
            Assert.Equal("completed", finished.State);
            Assert.Equal("video", finished.Kind);
            var afterJob = await History(client, first.ConversationId);
            var result = Assert.Single(afterJob.Messages, message => message.Id == finished.ResultMessageId);
            Assert.Contains(originalCitation + "#t=1", result.Content);
            Assert.Contains(afterJob.Messages, message => message.Id == first.Message.Id && message.Content == first.Message.Content);

            var audits = await db.LlmRequestAudits.Where(audit => audit.ConversationId == first.ConversationId).ToListAsync();
            Assert.Equal(4, audits.Count);
            Assert.All(audits, audit =>
            {
                using var payload = JsonDocument.Parse(audit.RequestPayloadJson);
                Assert.Equal("chat_attachments", payload.RootElement.GetProperty("type").GetString());
                Assert.DoesNotContain("data:image", audit.RequestPayloadJson);
                Assert.DoesNotContain(PrivateMarker, audit.RequestPayloadJson);
                Assert.Null(audit.ResponseBody);
            });
        });

    private static async Task<Guid> Upload(HttpClient client, string name, string type, byte[] data)
    {
        using var upload = await client.PostAsJsonAsync("/api/chat/uploads",
            new { fileName = name, contentType = type, sizeBytes = data.Length });
        upload.EnsureSuccessStatusCode();
        var id = (await upload.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        using var chunk = await client.PutAsync($"/api/chat/uploads/{id}/chunks/0", new ByteArrayContent(data));
        chunk.EnsureSuccessStatusCode();
        return id;
    }

    private static async Task<SendMessageResponse> Ask(HttpClient client, SendMessageRequest request)
    {
        using var response = await client.PostAsJsonAsync("/api/chat/messages", request);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<SendMessageResponse>())!;
    }

    private static async Task<ConversationResponse> History(HttpClient client, Guid id) =>
        (await client.GetFromJsonAsync<ConversationResponse>($"/api/chat/conversations/{id}"))!;

    private static async Task<byte[]> CreateVideoAsync()
    {
        var directory = Directory.CreateTempSubdirectory("aegis-video-chat-test-");
        try
        {
            var path = Path.Combine(directory.FullName, VideoName);
            var start = new ProcessStartInfo("/usr/bin/ffmpeg")
                { UseShellExecute = false, RedirectStandardError = true };
            foreach (var arg in new[]
            {
                "-nostdin", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc=duration=3:size=160x90:rate=10",
                "-vf", "format=yuv420p", "-filter_threads", "1", "-c:v", "libx264", "-threads", "1", path
            }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            var errors = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.True(process.ExitCode == 0, await errors);
            return await File.ReadAllBytesAsync(path);
        }
        finally { directory.Delete(recursive: true); }
    }

    private sealed class UnexpectedVideoSpeech : IVideoAttachmentAudio
    {
        public Task<VideoAttachmentTranscript> TranscribeAsync(AttachmentVideoMediaResult clip, CancellationToken ct) =>
            throw new InvalidOperationException("A visual-only inspection must not contact a transcription provider.");
    }

    private sealed class VideoModel : IAegisModelClient
    {
        public Task<ModelToolResponse> RespondWithToolsAsync(ModelToolRequest request, CancellationToken ct = default)
        {
            var (answer, calls) = React(request, worker: false);
            return Task.FromResult(new ModelToolResponse(answer, "fixture", "video", ModelPurpose.Chat,
                calls, [], null, null, Audit(request)));
        }

        public async IAsyncEnumerable<ModelToolStreamChunk> RespondWithToolsStreamAsync(ModelToolRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Yield();
            var (answer, calls) = React(request, worker: true);
            if (answer.Length > 0) yield return new(answer, false, [], []);
            yield return new(null, true, calls, [], Provider: "fixture", Model: "video", AuditData: Audit(request));
        }

        private static (string Answer, ModelToolCall[] Calls) React(ModelToolRequest request, bool worker)
        {
            var input = request.InputItems ?? request.Request.InputItems;
            Assert.NotNull(input);
            Assert.Contains(request.Tools, tool => tool.Name == AttachmentVideoTool.ToolName);
            Assert.Null(request.NativeSearchActionBudget);
            Assert.DoesNotContain(request.Tools, tool => tool.Name == PythonScriptTool.ToolName);
            if (!worker && request.Request.Input == DelegatedQuestion)
            {
                Assert.Contains(request.Tools, tool => tool.Name == "delegate_video_analysis");
                var args = JsonSerializer.SerializeToElement(new
                    { intent = "Vou examinar esse trecho do vídeo.", brief = "Examine amostra.mp4 entre 0,75 e 1,75 segundos." });
                return ("", [new("delegate", "delegate_video_analysis", args)]);
            }
            if (worker) Assert.DoesNotContain(request.Tools, tool => tool.Name == "delegate_video_analysis");

            var output = input.SingleOrDefault(item =>
                item.TryGetProperty("type", out var type) && type.GetString() == "function_call_output");
            var focused = worker || request.Request.Input.Contains("0,75", StringComparison.Ordinal);
            if (output.ValueKind == JsonValueKind.Undefined)
            {
                Assert.Contains(input, item => item.GetRawText().Contains(VideoName, StringComparison.Ordinal));
                var args = focused
                    ? JsonSerializer.SerializeToElement(new { id = 1, mode = "frames", start_seconds = .75, end_seconds = 1.75 })
                    : JsonSerializer.SerializeToElement(new { id = 1, mode = "frames" });
                return ("", [new("inspect", AttachmentVideoTool.ToolName, args)]);
            }

            Assert.Equal("inspect", output.GetProperty("call_id").GetString());
            Assert.DoesNotContain(request.AuditInputItems ?? [], item =>
                item.GetRawText().Contains("data:image", StringComparison.Ordinal));
            var parts = output.GetProperty("output").EnumerateArray().ToArray();
            using var metadata = JsonDocument.Parse(parts[0].GetProperty("text").GetString()!);
            Assert.Equal(VideoName, metadata.RootElement.GetProperty("video").GetString());
            Assert.Equal((parts.Length - 1) / 2, metadata.RootElement.GetProperty("frames").GetInt32());
            Assert.True(parts.Length >= 5);
            Assert.False(metadata.RootElement.GetProperty("speech_requested").GetBoolean());
            var lastTime = -1d;
            var citations = new List<string>();
            for (var index = 1; index < parts.Length; index += 2)
            {
                Assert.Equal("input_text", parts[index].GetProperty("type").GetString());
                var text = parts[index].GetProperty("text").GetString()!;
                var match = Regex.Match(text, @"^Quadro a (?<time>[0-9.]+) s\. Citação: (?<citation>.+)$");
                Assert.True(match.Success, text);
                var time = double.Parse(match.Groups["time"].Value, CultureInfo.InvariantCulture);
                Assert.InRange(time, focused ? .75 : 0, focused ? 1.75 : 3);
                Assert.True(time > lastTime, text);
                lastTime = time;
                var citation = match.Groups["citation"].Value;
                Assert.Contains("](aegis-video:", citation);
                Assert.EndsWith($"#t={(int)time})", citation);
                citations.Add(citation);

                Assert.Equal("input_image", parts[index + 1].GetProperty("type").GetString());
                var image = parts[index + 1].GetProperty("image_url").GetString()!;
                const string prefix = "data:image/jpeg;base64,";
                Assert.StartsWith(prefix, image);
                Assert.Equal(new byte[] { 0xff, 0xd8, 0xff }, Convert.FromBase64String(image[prefix.Length..])[..3]);
            }
            return ("A gravação de teste aparece em " + (focused ? citations[^1] : citations[0]) + ".", []);
        }

        private static LlmRequestAuditData Audit(ModelToolRequest request) =>
            new("fixture", "video", true, 1, JsonSerializer.Serialize(request.InputItems ?? request.Request.InputItems),
                200, PrivateMarker, null, null);
    }
}
