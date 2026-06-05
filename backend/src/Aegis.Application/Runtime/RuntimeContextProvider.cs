namespace Aegis.Application.Runtime;

public sealed class RuntimeContextProvider : IRuntimeContextProvider
{
    // TODO: Generate this context from real runtime inspection instead of using a static MVP string.
    private const string Context = """
        Operational context, for background only:
        I am currently available through chat.
        Use operational details only when directly relevant.
        Do not announce status or implementation details by default.
        """;

    public Task<string> GetRuntimeContextAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Context);
    }
}
