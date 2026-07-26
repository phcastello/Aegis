using System.Diagnostics.Metrics;

namespace Aegis.Application.Observability;

public sealed class AegisMetrics : IDisposable
{
    private readonly Meter meter = new("Aegis", "0.3.0");
    private int activeTurns;

    public Counter<long> TurnsStarted { get; }
    public Counter<long> TurnsCompleted { get; }
    public Counter<long> TurnsCancelled { get; }
    public Counter<long> TurnsFailed { get; }
    public Counter<long> LlmCancellations { get; }
    public Counter<long> LlmLateResultsDiscarded { get; }
    public Counter<long> TtsRequests { get; }
    public Counter<long> TtsFailures { get; }
    public Counter<long> TtsCancellations { get; }
    public Histogram<double> TurnCancellationSeconds { get; }
    public Histogram<double> TtsFirstAudioSeconds { get; }
    public Counter<long> TtsAudioBytes { get; }
    public Histogram<double> TtsStreamSeconds { get; }

    // Meter instruments are deliberately label-free: never attach turn/message UUIDs.
    public AegisMetrics()
    {
        meter.CreateObservableGauge("aegis_turns_active", () => Volatile.Read(ref activeTurns));
        TurnsStarted = meter.CreateCounter<long>("aegis_turns_started_total");
        TurnsCompleted = meter.CreateCounter<long>("aegis_turns_completed_total");
        TurnsCancelled = meter.CreateCounter<long>("aegis_turns_cancelled_total");
        TurnsFailed = meter.CreateCounter<long>("aegis_turns_failed_total");
        LlmCancellations = meter.CreateCounter<long>("aegis_llm_cancellations_total");
        LlmLateResultsDiscarded = meter.CreateCounter<long>("aegis_llm_late_results_discarded_total");
        TtsRequests = meter.CreateCounter<long>("aegis_tts_requests_total");
        TtsFailures = meter.CreateCounter<long>("aegis_tts_failures_total");
        TtsCancellations = meter.CreateCounter<long>("aegis_tts_cancellations_total");
        TurnCancellationSeconds = meter.CreateHistogram<double>("aegis_turn_cancellation_seconds");
        TtsFirstAudioSeconds = meter.CreateHistogram<double>("aegis_tts_first_audio_seconds");
        TtsAudioBytes = meter.CreateCounter<long>("aegis_tts_audio_bytes_total");
        TtsStreamSeconds = meter.CreateHistogram<double>("aegis_tts_stream_seconds");
    }

    public void ActiveTurnStarted() => Interlocked.Increment(ref activeTurns);
    public void ActiveTurnEnded() => InterlockedExtensions.ClampDecrement(ref activeTurns);
    public void Dispose() => meter.Dispose();
}

internal static class InterlockedExtensions
{
    public static void ClampDecrement(ref int value)
    {
        while (true)
        {
            var current = Volatile.Read(ref value);
            if (current <= 0 || Interlocked.CompareExchange(ref value, current - 1, current) == current) return;
        }
    }
}
