using Aegis.Application.Observability;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Aegis.Application.Tests;

[CollectionDefinition("LatencyProfileEnvironment", DisableParallelization = true)]
public sealed class LatencyProfileEnvironmentCollection { }

[Collection("LatencyProfileEnvironment")]
public sealed class TurnLatencyProfileTests
{
    private sealed class Logger : ILogger
    {
        public readonly List<string> Messages = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }

    [Fact]
    public async Task ConsumerScopePreservesCorrelationAndOriginAcrossIteratorYields()
    {
        var previous = Environment.GetEnvironmentVariable("AEGIS_LATENCY_PROFILE");
        Environment.SetEnvironmentVariable("AEGIS_LATENCY_PROFILE", "1");
        try
        {
            var logger = new Logger();
            var turnId = Guid.NewGuid();
            async IAsyncEnumerable<int> Stream()
            {
                using var nested = TurnLatencyProfile.Begin(turnId, logger);
                yield return 1;
                await Task.Yield();
                await TurnLatencyProfile.MeasureAsync("memory.embedding", () => Task.FromResult(1));
                TurnLatencyProfile.Mark("model.first_argument_delta");
                yield return 2;
            }
            // Mirrors the HTTP stream consumer; starting only inside Stream would
            // lose the ambient context when the next MoveNext starts after yield.
            using (TurnLatencyProfile.Begin(turnId, logger))
                await foreach (var item in Stream()) Assert.InRange(item, 1, 2);
            Assert.Single(logger.Messages, message => message.Contains(" chat.enter:"));
            Assert.Single(logger.Messages, message => message.Contains(" chat.exit:"));
            Assert.Contains(logger.Messages, message => message.Contains(" memory.embedding:"));
            Assert.Contains(logger.Messages, message => message.Contains(" model.first_argument_delta:"));
            Assert.All(logger.Messages, message => Assert.Contains(turnId.ToString(), message));
            var count = logger.Messages.Count;
            TurnLatencyProfile.Mark("must_not_escape_scope");
            Assert.Equal(count, logger.Messages.Count);
        }
        finally { Environment.SetEnvironmentVariable("AEGIS_LATENCY_PROFILE", previous); }
    }
}
