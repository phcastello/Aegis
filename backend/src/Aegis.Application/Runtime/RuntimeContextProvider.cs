namespace Aegis.Application.Runtime;

public sealed class RuntimeContextProvider : IRuntimeContextProvider
{
    // TODO: Generate this context from real runtime inspection instead of using a static MVP string.
    private const string Context =
        "Current operational context: I run inside the Aegis .NET backend. " +
        "The backend exposes a chat API that can be used by clients such as a PWA, curl, voice interfaces, or other future integrations. " +
        "Ollama is configured as the current LLM provider. " +
        "PostgreSQL is used for conversation persistence. " +
        "Qdrant is available in the environment, but semantic memory retrieval is not enabled yet.";

    public Task<string> GetRuntimeContextAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Context);
    }
}
