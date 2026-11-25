using System.Diagnostics;
using Aegis.Application.Observability;
using Aegis.Infrastructure.Voice;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Aegis.Application.Tests;

public sealed class SpeechCancellationDeadlineTests
{
    [Fact]
    public async Task UnresponsiveTtsCancellationHasDeadlineEvenWithInfiniteHttpClientTimeout()
    {
        using var http = new HttpClient(new HangingHandler()) { BaseAddress = new Uri("http://localhost/"), Timeout = Timeout.InfiniteTimeSpan };
        using var metrics = new AegisMetrics();
        var client = new AegisSpeechClient(http, Options.Create(new AegisTtsOptions { ConnectTimeoutSeconds = 1 }), NullLogger<AegisSpeechClient>.Instance, metrics);
        await client.CancelAsync("test-native-id").WaitAsync(TimeSpan.FromSeconds(4));
    }

    [Fact]
    public async Task ExplicitCallerCancellationStillPropagates()
    {
        using var http = new HttpClient(new HangingHandler()) { BaseAddress = new Uri("http://localhost/"), Timeout = Timeout.InfiniteTimeSpan };
        using var metrics = new AegisMetrics();
        var client = new AegisSpeechClient(http, Options.Create(new AegisTtsOptions()), NullLogger<AegisSpeechClient>.Instance, metrics);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.CancelAsync("test-native-id", cancellation.Token));
    }

    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.True(cancellationToken.CanBeCanceled);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }
}
