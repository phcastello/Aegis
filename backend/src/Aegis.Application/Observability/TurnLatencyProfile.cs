using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;

namespace Aegis.Application.Observability;

// Temporary detailed profiling is opt-in. No prompts, audio, provider payloads, or
// personal context are logged, and turn IDs are never metric labels.
public static class TurnLatencyProfile
{
    private sealed record Context(Guid TurnId, ILogger Logger, long Started, string Component);
    private static readonly AsyncLocal<Context?> Current = new();
    private sealed class Noop : IDisposable
    {
        public static readonly Noop Instance = new();
        public void Dispose() { }
    }

    public static IDisposable Begin(Guid turnId, ILogger? logger, string component = "chat")
    {
        if (logger is null || Environment.GetEnvironmentVariable("AEGIS_LATENCY_PROFILE") != "1")
            return Noop.Instance;
        // An outer HTTP/consumer scope survives async-iterator MoveNext boundaries.
        // Reuse it so every mark in that stream shares the same monotonic origin.
        if (Current.Value is { } active && active.TurnId == turnId && active.Component == component)
            return Noop.Instance;
        var previous = Current.Value;
        var context = new Context(turnId, logger, Stopwatch.GetTimestamp(), component);
        Current.Value = context;
        Mark(component + ".enter");
        return new Scope(previous, context);
    }

    public static IDisposable Measure(string stage, Histogram<double>? aggregateSeconds = null)
    {
        var context = Current.Value;
        return context is null && aggregateSeconds is null ? Noop.Instance :
            new Measurement(context, stage, aggregateSeconds);
    }

    public static async Task<T> MeasureAsync<T>(string stage, Func<Task<T>> operation)
    {
        using var measurement = Measure(stage);
        return await operation();
    }

    public static async Task MeasureAsync(string stage, Func<Task> operation)
    {
        using var measurement = Measure(stage);
        await operation();
    }

    public static void Mark(string stage, long? count = null)
    {
        if (Current.Value is { } context)
            Write(context, stage, 0, count);
    }

    private static void Write(Context context, string stage, double duration, long? count = null) =>
        context.Logger.LogInformation(
            "LatencyProfile {TurnId} {Stage}: elapsed={ElapsedMilliseconds:F3}ms duration={DurationMilliseconds:F3}ms count={Count}",
            context.TurnId, stage, Stopwatch.GetElapsedTime(context.Started).TotalMilliseconds, duration, count);

    private sealed class Measurement(Context? context, string stage, Histogram<double>? aggregateSeconds) : IDisposable
    {
        private readonly long started = Stopwatch.GetTimestamp();
        public void Dispose()
        {
            var elapsed = Stopwatch.GetElapsedTime(started);
            aggregateSeconds?.Record(elapsed.TotalSeconds);
            if (context is not null) Write(context, stage, elapsed.TotalMilliseconds);
        }
    }

    private sealed class Scope(Context? previous, Context context) : IDisposable
    {
        public void Dispose()
        {
            Write(context, context.Component + ".exit", Stopwatch.GetElapsedTime(context.Started).TotalMilliseconds);
            Current.Value = previous;
        }
    }
}
