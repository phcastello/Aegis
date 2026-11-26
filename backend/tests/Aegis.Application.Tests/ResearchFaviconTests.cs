using Aegis.Infrastructure.Chat;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using Xunit;

namespace Aegis.Application.Tests;

public sealed class ResearchFaviconTests
{
    private const string Host = "publisher.example.org";
    private const string Origin = "https://" + Host;
    private static readonly byte[] ValidPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAABAAAAAQCAYAAAAf8/9hAAAAGUlEQVR4nGMQClnwnxLMMGrAqAGjBgwXAwDf+AUfVDgLPAAAAABJRU5ErkJggg==");
    private static readonly byte[] Pixel = Convert.FromBase64String("R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7");

    [Fact]
    public async Task DeclaredIconWinsOverImplicitRootAndCachesByCanonicalHost()
    {
        using var transport = new TestTransport((uri, _) => Task.FromResult(uri.AbsolutePath switch
        {
            "/" => Html("""<html><head><link rel="shortcut icon" href="/theme/favicon.png?v=3"></head></html>"""),
            "/theme/favicon.png" => Image(ValidPng, "application/octet-stream"),
            "/favicon.ico" => Image(Pixel, "image/gif"),
            _ => Missing()
        }));
        using var resolver = new ResearchFaviconDiscovery(TimeProvider.System, transport);
        var actual = await resolver.FindAsync(Host);
        Assert.Equal(ResearchFaviconDiscovery.Status.Found, actual.Status);
        Assert.Equal("image/png", actual.Icon!.ContentType);
        Assert.Equal(ValidPng, actual.Icon.Bytes);
        Assert.Equal([Origin + "/", Origin + "/theme/favicon.png?v=3"], transport.Requests);
        Assert.Equal(actual, await resolver.FindAsync(Host.ToUpperInvariant()));
        Assert.Equal(2, transport.Requests.Count);
    }

    [Fact]
    public async Task FollowsSafeRedirectAndUsesFirstValidBaseForDeclaredCdnIcon()
    {
        using var transport = new TestTransport((uri, _) => Task.FromResult(uri.AbsoluteUri switch
        {
            Origin + "/" => Redirect("https://news.example.org/pt/"),
            "https://news.example.org/pt/" => Html("""
                <head>
                <base href="http://localhost/">
                <link rel=shortcut&#32;icon href=icons/a.png>
                <base href="https://public-assets.example.org/assets/">
                <base href="/must-be-ignored/">
                </head>
                """),
            "https://public-assets.example.org/assets/icons/a.png" => Image(ValidPng, "image/png"),
            _ => Missing()
        }));
        using var resolver = new ResearchFaviconDiscovery(TimeProvider.System, transport);
        Assert.Equal(ResearchFaviconDiscovery.Status.Found, (await resolver.FindAsync(Host)).Status);
        Assert.Equal([Origin + "/", "https://news.example.org/pt/", "https://public-assets.example.org/assets/icons/a.png"],
            transport.Requests);
    }

    [Fact]
    public async Task IgnoresSvgAndFakeLinksThenUsesNextValidCandidateAfterOnePixelPlaceholder()
    {
        using var transport = new TestTransport((uri, _) => Task.FromResult(uri.AbsolutePath switch
        {
            "/" => Html("""
                <head>
                  <!-- <link rel="icon" href="/comment.png"> -->
                  <script>const fake = '<link rel="icon" href="/script.png">';</script>
                  <meta property="og:image" content="/social.png">
                  <link href="/good.png" rel="icon" sizes="32x32">
                  <link href="/blank.gif" rel="icon" sizes="32x32">
                  <link rel="icon" type="image/svg+xml" href="/unsafe.svg">
                  <link rel="icon" href="/print.png" media="print" sizes="32x32">
                </head>
                <body><link rel="icon" href="/body.png">
                """),
            "/good.png" => Image(ValidPng, "image/png"),
            "/blank.gif" => Image(Pixel, "image/gif"),
            _ => Missing()
        }));
        using var resolver = new ResearchFaviconDiscovery(TimeProvider.System, transport);
        Assert.Equal(ResearchFaviconDiscovery.Status.Found, (await resolver.FindAsync(Host)).Status);
        Assert.Equal([Origin + "/", Origin + "/blank.gif", Origin + "/good.png"], transport.Requests);
    }

    [Fact]
    public async Task ReservedAndUnconventionalRedirectTargetsNeverReachTheTransport()
    {
        using var transport = new TestTransport((uri, _) => Task.FromResult(uri.AbsolutePath switch
        {
            "/" => Html("""
                <head>
                  <link rel="icon" href="http://localhost/icon.png">
                  <link rel="icon" href="https://asset.example.org:8443/icon.png">
                  <link rel="icon" href="/redirect.png">
                </head>
                """),
            "/redirect.png" => Redirect("http://127.0.0.1/icon.png"),
            "/favicon.ico" => Image(ValidPng, "image/png"),
            _ => Missing()
        }));
        using var resolver = new ResearchFaviconDiscovery(TimeProvider.System, transport);
        Assert.Equal(ResearchFaviconDiscovery.Status.Found, (await resolver.FindAsync(Host)).Status);
        Assert.Equal([Origin + "/", Origin + "/redirect.png", Origin + "/favicon.ico"], transport.Requests);

        Assert.Equal(ResearchFaviconDiscovery.Status.InvalidHost, (await resolver.FindAsync("http://" + Host)).Status);
        Assert.Equal(ResearchFaviconDiscovery.Status.InvalidHost, (await resolver.FindAsync("127.0.0.1")).Status);
        Assert.Equal(ResearchFaviconDiscovery.Status.InvalidHost, (await resolver.FindAsync(Host + "@public.example.org")).Status);
        Assert.Equal(3, transport.Requests.Count);
    }

    [Fact]
    public async Task ConfiguredAegisSitesAreExcludedAsInputBaseIconAndRedirectDestinations()
    {
        using var transport = new TestTransport((uri, _) => Task.FromResult(uri.AbsolutePath switch
        {
            "/" => Html("""
                <head>
                  <base href="https://aegis.example.org/private/">
                  <base href="/icons/">
                  <link rel="icon" href="https://www.aegis.example.org/icon.png">
                  <link rel="icon" href="redirect.png">
                </head>
                """),
            "/icons/redirect.png" => Redirect("https://api.example.org/api/restricted"),
            "/favicon.ico" => Image(ValidPng, "image/png"),
            _ => Missing()
        }));
        using var resolver = new ResearchFaviconDiscovery(TimeProvider.System, transport,
            ["https://aegis.example.org", "https://api.example.org/api/email/oauth/callback"]);
        Assert.Equal(ResearchFaviconDiscovery.Status.InvalidHost, (await resolver.FindAsync("www.aegis.example.org")).Status);
        Assert.Equal(ResearchFaviconDiscovery.Status.InvalidHost, (await resolver.FindAsync("api.example.org")).Status);
        Assert.Empty(transport.Requests);
        Assert.Equal(ResearchFaviconDiscovery.Status.Found, (await resolver.FindAsync(Host)).Status);
        Assert.Equal([Origin + "/", Origin + "/icons/redirect.png", Origin + "/favicon.ico"], transport.Requests);
    }

    [Fact]
    public async Task RetriesTheCanonicalWwwOriginAndCachesNegativeOutcomes()
    {
        using var transport = new TestTransport((uri, _) => Task.FromResult(uri.AbsoluteUri switch
        {
            "https://www." + Host + "/" => Html("<head><link rel=icon href=/icon.png></head>"),
            "https://www." + Host + "/icon.png" => Image(ValidPng, "image/png"),
            _ => Missing()
        }));
        using var resolver = new ResearchFaviconDiscovery(TimeProvider.System, transport);
        Assert.Equal(ResearchFaviconDiscovery.Status.Found, (await resolver.FindAsync(Host)).Status);
        Assert.Equal([Origin + "/", Origin + "/favicon.ico", "https://www." + Host + "/", "https://www." + Host + "/icon.png"],
            transport.Requests);
        Assert.Equal(ResearchFaviconDiscovery.Status.Missing, (await resolver.FindAsync("missing.example.org")).Status);
        var count = transport.Requests.Count;
        Assert.Equal(ResearchFaviconDiscovery.Status.Missing, (await resolver.FindAsync("missing.example.org")).Status);
        Assert.Equal(count, transport.Requests.Count);
    }

    [Fact]
    public async Task UsesOneGlobalRateForUncachedDiscoveriesWithoutChargingCacheHits()
    {
        using var transport = new TestTransport((_, _) => Task.FromResult(Missing()));
        using var resolver = new ResearchFaviconDiscovery(TimeProvider.System, transport);
        for (var i = 0; i < 36; ++i)
            Assert.Equal(ResearchFaviconDiscovery.Status.Missing, (await resolver.FindAsync($"host{i}.example.org")).Status);
        Assert.Equal(ResearchFaviconDiscovery.Status.Busy, (await resolver.FindAsync("another.example.org")).Status);
        var count = transport.Requests.Count;
        Assert.Equal(ResearchFaviconDiscovery.Status.Missing, (await resolver.FindAsync("host0.example.org")).Status);
        Assert.Equal(count, transport.Requests.Count);
        Assert.InRange(count, 36, 36 * 9);
    }

    [Fact]
    public async Task RefusesMoreThanFourConcurrentDiscoveryRequests()
    {
        var entered = 0;
        var fourEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var transport = new TestTransport(async (_, ct) =>
        {
            if (Interlocked.Increment(ref entered) == 4) fourEntered.TrySetResult();
            await release.Task.WaitAsync(ct);
            return Missing();
        });
        using var resolver = new ResearchFaviconDiscovery(TimeProvider.System, transport);
        var work = Enumerable.Range(0, 4).Select(i => resolver.FindAsync($"host{i}.example.org")).ToArray();
        try
        {
            await fourEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(ResearchFaviconDiscovery.Status.Busy, (await resolver.FindAsync("other.example.org")).Status);
        }
        finally { release.TrySetResult(); }
        await Task.WhenAll(work);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.5.0.8")]
    [InlineData("100.64.0.9")]
    [InlineData("168.63.129.16")]
    [InlineData("169.254.1.1")]
    [InlineData("172.20.7.1")]
    [InlineData("192.168.1.1")]
    [InlineData("192.0.2.1")]
    [InlineData("198.18.0.1")]
    [InlineData("198.51.100.1")]
    [InlineData("203.0.113.9")]
    [InlineData("224.0.0.1")]
    [InlineData("::1")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("64:ff9b::a00:1")]
    [InlineData("fc00::1")]
    [InlineData("fe80::1")]
    [InlineData("2001:db8::1")]
    [InlineData("2002::1")]
    [InlineData("3fff::1")]
    public void RejectsNonPublicAndMixedDnsAnswers(string address)
    {
        Assert.False(ResearchFaviconNetwork.ArePublicAddresses([IPAddress.Parse(address)]));
        Assert.False(ResearchFaviconNetwork.ArePublicAddresses([IPAddress.Parse("8.8.8.8"), IPAddress.Parse(address)]));
    }

    [Fact]
    public void RequiresAtLeastOneBoundedPublicDnsAnswerAndARestrictedHttpHandler()
    {
        Assert.True(ResearchFaviconNetwork.ArePublicAddresses([IPAddress.Parse("8.8.8.8"), IPAddress.Parse("2606:4700::1111")]));
        Assert.False(ResearchFaviconNetwork.ArePublicAddresses([]));
        Assert.False(ResearchFaviconNetwork.ArePublicAddresses(Enumerable.Repeat(IPAddress.Parse("8.8.8.8"), 17).ToArray()));
        using var handler = ResearchFaviconNetwork.CreateHandler();
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseProxy);
        Assert.False(handler.UseCookies);
        Assert.Null(handler.Credentials);
        Assert.Null(handler.ActivityHeadersPropagator);
        Assert.True(handler.SslOptions.CertificateChainPolicy!.DisableCertificateDownloads);
        Assert.Null(handler.SslOptions.RemoteCertificateValidationCallback);
        Assert.NotNull(handler.ConnectCallback);
    }

    [Fact]
    public void RejectsBadImageDimensionsSvgHtmlAndTruncatedFormats()
    {
        Assert.Equal("image/png", ResearchFaviconRaster.ContentType(ValidPng));
        Assert.Null(ResearchFaviconRaster.ContentType(Pixel));
        Assert.Null(ResearchFaviconRaster.ContentType("<svg viewBox='0 0 256 256'></svg>"u8));
        Assert.Null(ResearchFaviconRaster.ContentType("<!doctype html><html></html>"u8));
        Assert.Null(ResearchFaviconRaster.ContentType(ValidPng.AsSpan(0, ValidPng.Length - 12)));
        var oversized = ValidPng.ToArray();
        oversized[18] = 64;
        Assert.Null(ResearchFaviconRaster.ContentType(oversized));
    }

    private static HttpResponseMessage Html(string text) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(text, System.Text.Encoding.UTF8, "text/html")
    };

    private static HttpResponseMessage Image(byte[] bytes, string contentType)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return response;
    }

    private static HttpResponseMessage Redirect(string target)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(target, UriKind.RelativeOrAbsolute);
        return response;
    }

    private static HttpResponseMessage Missing() => new(HttpStatusCode.NotFound);

    private sealed class TestTransport(Func<Uri, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public readonly ConcurrentQueue<string> Requests = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Null(request.Headers.Referrer);
            Assert.Null(request.Headers.Authorization);
            Assert.False(request.Headers.Contains("Cookie"));
            Requests.Enqueue(request.RequestUri!.AbsoluteUri);
            return send(request.RequestUri, cancellationToken);
        }
    }
}
