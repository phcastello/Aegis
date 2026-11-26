using System.Diagnostics;
using Aegis.Application.Observability;
using Aegis.Application.Voice;
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

    [Fact]
    public async Task EmptySpeechYieldsLocalSilenceWithoutCallingTheSynthesizer()
    {
        // This handler requires cancellation and could not produce a response.
        using var http = new HttpClient(new HangingHandler());
        using var metrics = new AegisMetrics();
        var client = new AegisSpeechClient(http, Options.Create(new AegisTtsOptions()), NullLogger<AegisSpeechClient>.Instance, metrics);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await using var speech = await client.StreamSpeechAsync(
            new SpeechRequest(Guid.NewGuid(), Guid.NewGuid(), "silent-unit", "", Guid.NewGuid()), deadline.Token);
        using var buffer = new MemoryStream();
        await speech.Stream.CopyToAsync(buffer, deadline.Token);

        Assert.Equal("pcm_s16le", speech.Format.Format);
        Assert.Equal(24000, speech.Format.SampleRate);
        Assert.Equal(1, speech.Format.Channels);
        Assert.Equal(new byte[960], buffer.ToArray());
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
