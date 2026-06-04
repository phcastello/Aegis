namespace Aegis.Application.Runtime;

public interface IRuntimeContextProvider
{
    Task<string> GetRuntimeContextAsync(CancellationToken cancellationToken = default);
}
