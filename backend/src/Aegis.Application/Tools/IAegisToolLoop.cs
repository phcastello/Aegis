using Aegis.Application.Models;
using Aegis.Application.Voice;

namespace Aegis.Application.Tools;

public interface IAegisToolLoop
{
    Task<ProvisionalVoiceDisposition> DecideProvisionalVoiceInterruptionAsync(
        string previousRequest, string candidate, string? playbackContext, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    Task<ModelToolResponse> RunAsync(
        ModelRequest request,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<ModelStreamChunk> StreamAsync(
        ModelRequest request,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default);
}
