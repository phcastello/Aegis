using System.Diagnostics.Metrics;

namespace Aegis.Application.Observability;

public sealed class AegisMetrics : IDisposable
{
    private readonly Meter meter = new("Aegis", "0.3.2");
    private int activeTurns;

    public Counter<long> TurnsStarted { get; }
    public Counter<long> TurnsCompleted { get; }
    public Counter<long> TurnsCancelled { get; }
    public Counter<long> TurnsFailed { get; }
    public Counter<long> LlmCancellations { get; }
    public Counter<long> LlmLateResultsDiscarded { get; }
    public Counter<long> LlmModelCalls { get; }
    public Counter<long> LlmToolCalls { get; }
    public Counter<long> LlmInputTokens { get; }
    public Counter<long> LlmCachedInputTokens { get; }
    public Counter<long> LlmCacheWriteTokens { get; }
    public Counter<long> LlmOutputTokens { get; }
    public Histogram<long> LlmToolIterations { get; }
    public Histogram<long> LlmTurnModelCalls { get; }
    public Histogram<long> LlmTurnToolCalls { get; }
    public Histogram<double> LlmTurnSeconds { get; }
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
        LlmModelCalls = meter.CreateCounter<long>("aegis_llm_model_calls_total");
        LlmToolCalls = meter.CreateCounter<long>("aegis_llm_tool_calls_total");
        LlmInputTokens = meter.CreateCounter<long>("aegis_llm_input_tokens_total");
        LlmCachedInputTokens = meter.CreateCounter<long>("aegis_llm_cached_input_tokens_total");
        LlmCacheWriteTokens = meter.CreateCounter<long>("aegis_llm_cache_write_tokens_total");
        LlmOutputTokens = meter.CreateCounter<long>("aegis_llm_output_tokens_total");
        LlmToolIterations = meter.CreateHistogram<long>("aegis_llm_tool_iterations");
        LlmTurnModelCalls = meter.CreateHistogram<long>("aegis_llm_turn_model_calls");
        LlmTurnToolCalls = meter.CreateHistogram<long>("aegis_llm_turn_tool_calls");
        LlmTurnSeconds = meter.CreateHistogram<double>("aegis_llm_turn_seconds");
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
