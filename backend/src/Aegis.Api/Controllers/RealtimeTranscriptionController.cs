using System.Net.WebSockets;
using System.Security.Claims;
using Aegis.Api.Nodes;
using Aegis.Application.Nodes;
using Aegis.Infrastructure.Voice.Transcription;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Aegis.Api.Controllers;

[ApiController, Route("api/voice/transcriptions/realtime"), Authorize(Policy = "NodeManagement")]
public sealed class RealtimeTranscriptionController(ElevenLabsRealtimeTranscription relay, RealtimeTranscriptionCapacity capacity,
    IServiceScopeFactory scopes, ILogger<RealtimeTranscriptionController> logger) : ControllerBase
{
    [HttpGet]
    public async Task Connect(CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        if (!HttpContext.WebSockets.IsWebSocketRequest) { Response.StatusCode = 400; return; }
        if (Request.Query.Count != 1 || !Guid.TryParse(Request.Query["transcriptionRequestId"], out var requestId) || requestId == Guid.Empty)
            { Response.StatusCode = 400; return; }
        if (!relay.IsConfigured) { Response.StatusCode = 503; return; }
        var nodeId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        using var lease = capacity.TryAcquire(nodeId);
        if (lease is null) { Response.StatusCode = 429; return; }
        var credential = Request.Headers.Authorization.ToString()[ (NodeAuthenticationHandler.SchemeName.Length + 1).. ];
        using var socket = await HttpContext.WebSockets.AcceptWebSocketAsync();
        var reason = "completed";
        try
        {
            var result = await relay.RunAsync(socket, requestId, async ct => {
                // Fresh scope avoids a tracked entity hiding disable/revoke during the capture.
                using var scope = scopes.CreateScope();
                var authentication = await scope.ServiceProvider.GetRequiredService<INodeRegistry>().AuthenticateAsync(credential, ct);
                if (!authentication.Authenticated || authentication.Node!.Id != nodeId)
                    throw new RealtimeTranscriptionException("node_identity_changed");
            }, cancellationToken);
            logger.LogInformation("aegis_stt_realtime TranscriptionRequestId={TranscriptionRequestId} Model={Model} AudioBytes={AudioBytes} CommitMilliseconds={CommitMilliseconds} ConfirmedSegments={ConfirmedSegments}",
                requestId, ElevenLabsRealtimeTranscription.Model, result.AudioBytes, result.CommitMilliseconds, result.ConfirmedSegments);
        }
        catch (RealtimeTranscriptionException error) { reason = error.Code; }
        catch (OperationCanceledException) { reason = cancellationToken.IsCancellationRequested ? "cancelled" : "timeout"; }
        catch (Exception error)
        {
            reason = "realtime_unavailable";
            logger.LogWarning("aegis_stt_realtime_failed TranscriptionRequestId={TranscriptionRequestId} ErrorType={ErrorType}", requestId, error.GetType().Name);
        }
        finally
        {
            // RunAsync has already ended the paid upstream. Release admission before the
            // peer observes close/error and immediately starts its next bounded attempt.
            lease.Dispose();
            // Public errors contain codes only. Provider errors may contain credentials/content.
            using var close = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                if (socket.State == WebSocketState.Open && reason != "completed")
                    await ElevenLabsRealtimeTranscription.SendJson(socket, new { type = "error", transcriptionRequestId = requestId, code = reason }, close.Token);
                if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    await socket.CloseOutputAsync(reason == "completed" ? WebSocketCloseStatus.NormalClosure : WebSocketCloseStatus.PolicyViolation, reason, close.Token);
            }
            catch (Exception error) when (error is OperationCanceledException or WebSocketException or ObjectDisposedException or IOException) { socket.Abort(); }
            if (reason != "completed") logger.LogInformation("aegis_stt_realtime_ended TranscriptionRequestId={TranscriptionRequestId} Reason={Reason}", requestId, reason);
        }
    }
}

/// <summary>Paid sessions exist only inside active turns: one per native identity, four total.</summary>
public sealed class RealtimeTranscriptionCapacity
{
    private readonly HashSet<Guid> active = [];
    public IDisposable? TryAcquire(Guid nodeId)
    {
        lock (active) { if (active.Count >= 4 || !active.Add(nodeId)) return null; }
        return new Lease(this, nodeId);
    }
    private sealed class Lease(RealtimeTranscriptionCapacity owner, Guid nodeId) : IDisposable
    {
        private int disposed;
        public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) lock (owner.active) owner.active.Remove(nodeId); }
    }
}
