using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Aegis.Infrastructure.Chat;

public sealed class ResearchFaviconDiscovery : IDisposable
{
    private const int MaximumHtmlBytes = 96 * 1024;
    private const int MaximumIconBytes = 160 * 1024;
    private const int MaximumRedirects = 2;
    private const int MaximumRequests = 9;
    private const int MaximumNewDiscoveriesPerMinute = 36;
    private readonly MemoryCache cache = new(new MemoryCacheOptions { SizeLimit = 8 * 1024 });
    private readonly SemaphoreSlim discoverySlots = new(4, 4);
    private readonly Queue<long> recentDiscoveries = new();
    private readonly TimeProvider clock;
    private readonly HttpClient client;
    private readonly HashSet<string> ownHosts = new(StringComparer.OrdinalIgnoreCase);

    public ResearchFaviconDiscovery(TimeProvider clock, IConfiguration configuration) :
        this(clock, ResearchFaviconNetwork.CreateHandler(), [
            configuration["AEGIS_PUBLIC_APP_URL"], configuration["Google:PublicAppUrl"],
            configuration["GOOGLE_REDIRECT_URI"], configuration["Google:RedirectUri"]
        ]) { }

    internal ResearchFaviconDiscovery(TimeProvider clock, HttpMessageHandler handler, IEnumerable<string?>? ownUrls = null)
    {
        this.clock = clock;
        client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        foreach (var url in ownUrls ?? [])
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" &&
                ResearchFaviconNetwork.NormalizeHost(uri.IdnHost.TrimEnd('.')) is { } host)
                ownHosts.Add(WithoutWww(host));
        }
    }

    private static string WithoutWww(string host) =>
        host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;

    private bool IsOwnHost(string host) => ownHosts.Contains(WithoutWww(host.TrimEnd('.')));

    private bool IsAllowedDestination(Uri uri) =>
        ResearchFaviconNetwork.IsAllowedDestination(uri) && !IsOwnHost(uri.IdnHost);

    public enum Status { Found, Missing, Busy, InvalidHost }
    public sealed record Icon(byte[] Bytes, string ContentType);
    public sealed record Result(Status Status, Icon? Icon = null);
    private sealed record FetchResponse(Uri Uri, byte[] Bytes);
    private sealed class RequestBudget { public int RequestsLeft = MaximumRequests; }

    public async Task<Result> FindAsync(string? value, CancellationToken cancellationToken = default)
    {
        var host = ResearchFaviconNetwork.NormalizeHost(value);
        if (host is null || IsOwnHost(host)) return new(Status.InvalidHost);
        if (cache.TryGetValue<Result>(host, out var cached)) return cached!;

        if (!discoverySlots.Wait(0, cancellationToken)) return new(Status.Busy);
        try
        {
            if (cache.TryGetValue<Result>(host, out cached)) return cached!;
            if (!TryReserveDiscovery()) return new(Status.Busy);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5), clock);
            using var ct = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
            Icon? icon;
            try { icon = await DiscoverAsync(host, ct.Token); }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                icon = null;
            }
            var result = icon is null ? new Result(Status.Missing) : new Result(Status.Found, icon);
            cache.Set(host, result, new MemoryCacheEntryOptions()
                .SetSize(Math.Max(8, (icon?.Bytes.Length ?? 0) / 1024 + 1))
                .SetAbsoluteExpiration(icon is null ? TimeSpan.FromMinutes(15) : TimeSpan.FromHours(12)));
            return result;
        }
        finally { discoverySlots.Release(); }
    }

    private bool TryReserveDiscovery()
    {
        lock (recentDiscoveries)
        {
            var now = clock.GetTimestamp();
            while (recentDiscoveries.TryPeek(out var oldest) && clock.GetElapsedTime(oldest, now) >= TimeSpan.FromMinutes(1))
                recentDiscoveries.Dequeue();
            if (recentDiscoveries.Count >= MaximumNewDiscoveriesPerMinute) return false;
            recentDiscoveries.Enqueue(now);
            return true;
        }
    }

    private async Task<Icon?> DiscoverAsync(string host, CancellationToken ct)
    {
        var budget = new RequestBudget();
        var tried = new HashSet<Uri>();
        foreach (var origin in Origins(host))
        {
            if (budget.RequestsLeft == 0) break;
            var page = await FetchAsync(origin, budget, html: true, ct);
            ResearchFaviconHtml.Candidates? declared = null;
            if (page is not null)
            {
                try { declared = ResearchFaviconHtml.Read(page.Uri, Encoding.UTF8.GetString(page.Bytes), IsAllowedDestination); }
                catch (RegexMatchTimeoutException) { }
            }

            foreach (var url in declared?.Icons ?? [])
            {
                if (await TryIconAsync(url, budget, tried, ct) is { } result) return result;
            }

            // The implicit icon belongs to the final document origin when the home page redirects.
            var defaultIcon = new Uri(page?.Uri ?? origin, "/favicon.ico");
            if (await TryIconAsync(defaultIcon, budget, tried, ct) is { } implicitIcon) return implicitIcon;
            foreach (var url in declared?.TouchIcons ?? [])
            {
                if (await TryIconAsync(url, budget, tried, ct) is { } result) return result;
            }
        }
        return null;
    }

    private static IEnumerable<Uri> Origins(string host)
    {
        yield return new UriBuilder(Uri.UriSchemeHttps, host).Uri;
        if (!host.StartsWith("www.", StringComparison.Ordinal) && host.Length <= 249)
            yield return new UriBuilder(Uri.UriSchemeHttps, "www." + host).Uri;
        yield return new UriBuilder(Uri.UriSchemeHttp, host).Uri;
    }

    private async Task<Icon?> TryIconAsync(Uri uri, RequestBudget budget, HashSet<Uri> tried, CancellationToken ct)
    {
        if (!tried.Add(uri)) return null;
        var file = await FetchAsync(uri, budget, html: false, ct);
        return file is not null && ResearchFaviconRaster.ContentType(file.Bytes) is { } media
            ? new Icon(file.Bytes, media) : null;
    }

    private async Task<FetchResponse?> FetchAsync(Uri target, RequestBudget budget, bool html, CancellationToken ct)
    {
        try
        {
            for (var redirects = 0; redirects <= MaximumRedirects && budget.RequestsLeft > 0; ++redirects)
            {
                if (!IsAllowedDestination(target)) return null;
                budget.RequestsLeft--;
                using var request = new HttpRequestMessage(HttpMethod.Get, target)
                {
                    Version = HttpVersion.Version11,
                    VersionPolicy = HttpVersionPolicy.RequestVersionExact
                };
                request.Headers.UserAgent.ParseAdd("AegisFavicon/1.0");
                request.Headers.Accept.ParseAdd(html ? "text/html, application/xhtml+xml" : "image/*");
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
                {
                    var redirected = ResearchFaviconNetwork.ResolveLink(target, response.Headers.Location?.OriginalString);
                    if (redirected is null || !IsAllowedDestination(redirected) || redirected == target) return null;
                    target = redirected;
                    continue;
                }

                if (response.StatusCode != HttpStatusCode.OK) return null;
                var media = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
                if (html && media is not (null or "text/html" or "application/xhtml+xml")) return null;
                if (!html && (media is "text/html" or "application/xhtml+xml" or "image/svg+xml" ||
                    response.Content.Headers.ContentLength > MaximumIconBytes)) return null;

                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                var bytes = await ReadBoundedAsync(stream, html ? MaximumHtmlBytes : MaximumIconBytes,
                    allowPrefix: html, ct);
                return bytes is null ? null : new FetchResponse(target, bytes);
            }
        }
        catch (HttpRequestException) { }
        catch (IOException) { }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        return null;
    }

    private static async Task<byte[]?> ReadBoundedAsync(Stream stream, int limit, bool allowPrefix, CancellationToken ct)
    {
        var buffer = new byte[limit + (allowPrefix ? 0 : 1)];
        var count = 0;
        while (count < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(count), ct);
            if (read == 0) break;
            count += read;
        }
        return count > limit || count == 0 ? null : buffer.AsSpan(0, count).ToArray();
    }

    public void Dispose()
    {
        client.Dispose();
        cache.Dispose();
        discoverySlots.Dispose();
    }
}
