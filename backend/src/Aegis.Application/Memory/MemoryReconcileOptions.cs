namespace Aegis.Application.Memory;

public sealed class MemoryReconcileOptions
{
    // Zero disables periodic reconciliation; startup reconciliation still runs.
    public int IntervalMinutes { get; set; } = 30;
}
