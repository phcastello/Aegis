namespace Aegis.Application.Models;

public interface IAegisModelClient
{
    Task<ModelToolResponse> RespondWithToolsAsync(
        ModelToolRequest request,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<ModelToolStreamChunk> RespondWithToolsStreamAsync(
        ModelToolRequest request,
        CancellationToken cancellationToken = default);
}
