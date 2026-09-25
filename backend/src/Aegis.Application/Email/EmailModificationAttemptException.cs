namespace Aegis.Application.Email;

public sealed class EmailModificationAttemptException(
    bool requestWasSent,
    int completedCount,
    Exception innerException)
    : Exception("Gmail batch modification failed.", innerException)
{
    public bool RequestWasSent { get; } = requestWasSent;

    public int CompletedCount { get; } = completedCount;
}

public sealed class EmailModificationCancelledException(
    bool requestWasSent,
    int completedCount,
    OperationCanceledException innerException,
    CancellationToken cancellationToken)
    : OperationCanceledException("Gmail batch modification was cancelled.", innerException, cancellationToken)
{
    public bool RequestWasSent { get; } = requestWasSent;

    public int CompletedCount { get; } = completedCount;
}
