namespace Aegis.Application.Models;

public interface IAegisModelClient
{
    Task<ModelCompletionResponse> GenerateAsync(
        ModelRequest request,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<ModelStreamChunk> StreamAsync(
        ModelRequest request,
        CancellationToken cancellationToken = default);

    Task<ModelToolResponse> RespondWithToolsAsync(
        ModelToolRequest request,
        CancellationToken cancellationToken = default);
}
