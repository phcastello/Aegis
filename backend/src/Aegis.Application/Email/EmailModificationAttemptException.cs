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
