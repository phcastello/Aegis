namespace Aegis.Application.Llm;

public interface ILlmClient
{
    Task<LlmCompletionResponse> GenerateAsync(
        string prompt,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<LlmStreamChunk> StreamCompletionAsync(
        string prompt,
        CancellationToken cancellationToken = default);
}
