namespace Aegis.Application.Tools;

public interface IPythonScriptRunner
{
    bool IsAvailable { get; }
    Task<PythonScriptResult> RunAsync(string code, string? input, CancellationToken cancellationToken);
}

public sealed record PythonScriptResult(bool Success, string Stdout, string Stderr, string? ErrorCode = null);
