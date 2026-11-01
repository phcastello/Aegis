using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Aegis.Infrastructure.Voice.Transcription;

/// <summary>Creates a server-only connection; provider credentials never enter the native contract.</summary>
public interface IRealtimeTranscriptionConnector
{
    Task<WebSocket> ConnectAsync(Uri uri, string apiKey, CancellationToken cancellationToken);
}

public sealed class RealtimeTranscriptionConnector : IRealtimeTranscriptionConnector
{
    public async Task<WebSocket> ConnectAsync(Uri uri, string apiKey, CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("xi-api-key", apiKey);
        socket.Options.KeepAliveInterval = TimeSpan.Zero;
        try { await socket.ConnectAsync(uri, cancellationToken); return socket; }
        catch { socket.Dispose(); throw; }
    }
}

public sealed class RealtimeTranscriptionException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

public sealed record RealtimeTranscriptionResult(long AudioBytes, long CommitMilliseconds, int ConfirmedSegments);

/// <summary>
/// One bounded local turn, manual commit only. This transport has no reference to chat or tools.
/// A provider committed segment is accumulated; only a local commit can authorize a final result.
/// </summary>
public sealed class ElevenLabsRealtimeTranscription(
    IRealtimeTranscriptionConnector connector,
    IOptions<ElevenLabsSttOptions> providerOptions,
    IOptions<SttOptions> options)
{
    public const string Model = "scribe_v2_realtime";
    public const int MinimumAudioBytes = 2;
    public const int MaximumAudioBytes = 23 * 16000 * 2;
    public const int MaximumFrameBytes = 64000;
    public const int MaximumTextCharacters = 16000;
    public bool IsConfigured => options.Value.Enabled && options.Value.RealtimeEnabled &&
        options.Value.PrimaryProvider.Equals("elevenlabs", StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(providerOptions.Value.ApiKey);

    public Uri ProviderUri()
    {
        var baseUri = new Uri(providerOptions.Value.BaseUrl.TrimEnd('/') + "/");
        if (baseUri.Scheme is not ("https" or "http")) throw new RealtimeTranscriptionException("provider_unavailable");
        var builder = new UriBuilder(new Uri(baseUri, "v1/speech-to-text/realtime")) {
            Scheme = baseUri.Scheme == "https" ? "wss" : "ws",
            Query = "model_id=" + Model + "&audio_format=pcm_16000&commit_strategy=manual&include_timestamps=false&include_language_detection=false&no_verbatim=false&language_code=" +
                Uri.EscapeDataString(Language(options.Value.Language))
        };
        return builder.Uri;
    }

    public async Task<RealtimeTranscriptionResult> RunAsync(WebSocket client, Guid requestId,
        Func<CancellationToken, Task> authorizeFinal, CancellationToken cancellationToken)
    {
        using var wholeTurn = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        using var commitDeadline = new CancellationTokenSource();
        using var session = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, wholeTurn.Token, commitDeadline.Token);
        using var connect = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
        connect.CancelAfter(TimeSpan.FromSeconds(8));
        using var upstream = await connector.ConnectAsync(ProviderUri(), providerOptions.Value.ApiKey, connect.Token);
        try
        {
            using (var started = await ReceiveJson(upstream, 32768, connect.Token))
            {
                var root = started.RootElement;
                if (Type(root) != "session_started") throw new RealtimeTranscriptionException("provider_unavailable");
                if (!root.TryGetProperty("config", out var config) || !ManualCommit(config) ||
                    !config.TryGetProperty("sample_rate", out var rate) || rate.GetInt32() != 16000 ||
                    !config.TryGetProperty("model_id", out var model) || model.GetString() != Model)
                    throw new RealtimeTranscriptionException("provider_configuration_mismatch");
            }
            await SendJson(client, new { type = "ready", transcriptionRequestId = requestId,
                model = Model, minAudioMilliseconds = 0 }, session.Token);
            var committed = 0;
            long audioBytes = 0;
            var commitClock = new System.Diagnostics.Stopwatch();
            using var commitGate = new SemaphoreSlim(1);
            var input = SendInput();
            var output = ReceiveOutput();
            try
            {
                var first = await Task.WhenAny(input, output);
                // Input only completes normally on disconnect; commit keeps monitoring client cancellation.
                if (first == input) { await input; throw new RealtimeTranscriptionException("client_disconnected"); }
                var result = await output;
                await authorizeFinal(session.Token);
                await SendJson(client, new { type = "final", transcriptionRequestId = requestId, text = result.Text }, session.Token);
                return new(audioBytes, commitClock.ElapsedMilliseconds, result.Count);
            }
            finally
            {
                session.Cancel();
                upstream.Abort();
                try { await Task.WhenAll(input, output); }
                catch (Exception e) when (e is OperationCanceledException or WebSocketException or RealtimeTranscriptionException or JsonException) { }
            }

            async Task SendInput()
            {
                while (true)
                {
                    var (kind, payload) = await ReceiveMessage(client, MaximumFrameBytes, session.Token);
                    if (kind == WebSocketMessageType.Close) return;
                    if (Volatile.Read(ref committed) != 0) throw new RealtimeTranscriptionException("already_committed");
                    if (kind == WebSocketMessageType.Binary)
                    {
                        if (payload.Length == 0 || payload.Length % 2 != 0) throw new RealtimeTranscriptionException("invalid_audio");
                        audioBytes += payload.Length;
                        if (audioBytes > MaximumAudioBytes) throw new RealtimeTranscriptionException("audio_limit_exceeded");
                        await SendAudio(upstream, payload, false, session.Token);
                        continue;
                    }
                    using var message = JsonDocument.Parse(payload);
                    if (!message.RootElement.TryGetProperty("type", out var type) || type.GetString() != "commit" ||
                        message.RootElement.EnumerateObject().Count() != 1)
                        throw new RealtimeTranscriptionException("invalid_commit");
                    if (audioBytes < MinimumAudioBytes) throw new RealtimeTranscriptionException("audio_too_short");
                    await authorizeFinal(session.Token);
                    await commitGate.WaitAsync(session.Token);
                    try
                    {
                        commitClock.Start();
                        await SendAudio(upstream, [], true, session.Token);
                        Volatile.Write(ref committed, 1);
                        commitDeadline.CancelAfter(TimeSpan.FromSeconds(10));
                    }
                    finally { commitGate.Release(); }
                }
            }
            async Task<(string Text, int Count)> ReceiveOutput()
            {
                var text = new StringBuilder();
                var count = 0;
                while (true)
                {
                    using var message = await ReceiveJson(upstream, 65536, session.Token);
                    var root = message.RootElement;
                    var type = Type(root);
                    if (type == "partial_transcript") continue;
                    // The provider emits a second representation of each segment when timestamps
                    // are enabled. Never append both, and never deduplicate by text ("não, não").
                    if (type == "committed_transcript_with_timestamps") continue;
                    if (type != "committed_transcript") throw new RealtimeTranscriptionException("provider_transcription_failed");
                    var segment = root.GetProperty("text").GetString()?.Trim() ?? "";
                    if (segment.Length > 0)
                    {
                        if (text.Length > 0) text.Append(' ');
                        text.Append(segment);
                        if (text.Length > MaximumTextCharacters || ++count > 32) throw new RealtimeTranscriptionException("transcript_limit_exceeded");
                    }
                    await commitGate.WaitAsync(session.Token);
                    try { if (Volatile.Read(ref committed) != 0) return (text.ToString(), count); }
                    finally { commitGate.Release(); }
                }
            }
        }
        finally { upstream.Abort(); }
    }

    private static string Language(string language) => language.Trim().ToLowerInvariant() switch { "por" or "pt-br" => "pt", _ => language.Trim() };
    private static bool ManualCommit(JsonElement config) =>
        config.TryGetProperty("commit_strategy", out var strategy) ? strategy.GetString() == "manual" :
        config.TryGetProperty("vad_commit_strategy", out var vad) && vad.ValueKind == JsonValueKind.False;
    private static string? Type(JsonElement message) => message.TryGetProperty("message_type", out var type) ? type.GetString() : null;
    private static Task SendAudio(WebSocket socket, byte[] audio, bool commit, CancellationToken ct) =>
        SendJson(socket, new { message_type = "input_audio_chunk", audio_base_64 = Convert.ToBase64String(audio), commit, sample_rate = 16000 }, ct);
    public static Task SendJson(WebSocket socket, object value, CancellationToken ct) =>
        socket.SendAsync(new ArraySegment<byte>(JsonSerializer.SerializeToUtf8Bytes(value)), WebSocketMessageType.Text, true, ct);
    private static async Task<JsonDocument> ReceiveJson(WebSocket socket, int maximum, CancellationToken ct)
    {
        var (type, payload) = await ReceiveMessage(socket, maximum, ct);
        if (type != WebSocketMessageType.Text) throw new RealtimeTranscriptionException("provider_disconnected");
        return JsonDocument.Parse(payload);
    }
    private static async Task<(WebSocketMessageType Type, byte[] Payload)> ReceiveMessage(WebSocket socket, int maximum, CancellationToken ct)
    {
        using var message = new MemoryStream();
        var buffer = new byte[Math.Min(maximum, 8192)];
        WebSocketMessageType? kind = null;
        while (true)
        {
            var part = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            if (part.MessageType == WebSocketMessageType.Close) return (part.MessageType, []);
            if (kind.HasValue && kind != part.MessageType) throw new RealtimeTranscriptionException("invalid_message");
            kind = part.MessageType;
            if (message.Length + part.Count > maximum) throw new RealtimeTranscriptionException("frame_limit_exceeded");
            message.Write(buffer, 0, part.Count);
            if (part.EndOfMessage) return (part.MessageType, message.ToArray());
        }
    }
}
