namespace Aegis.Application.Tools;

public interface IPythonScriptRunner
{
    bool IsAvailable { get; }
    Task<PythonScriptResult> RunAsync(string code, string? input, IReadOnlyList<string> inputFiles,
        ToolExecutionContext context, CancellationToken cancellationToken);
}

public sealed record PythonScriptResult(bool Success, string Stdout, string Stderr, string? ErrorCode = null,
    IReadOnlyList<string>? GeneratedFiles = null);

public interface IScriptWorkspaceCleanup
{
    Task DeleteAsync(Guid conversationId, CancellationToken ct);
}
