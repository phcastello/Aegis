using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Aegis.Infrastructure.Chat;

internal static class ResearchFaviconNetwork
{
    private static readonly HashSet<string> NonPublicSuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "alt", "arpa", "corp", "example", "home", "internal", "invalid", "lan", "local", "localhost", "onion", "test"
    };

    internal static string? NormalizeHost(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 253 || IPAddress.TryParse(value, out _)) return null;
        var labels = value.Split('.');
        if (labels.Length < 2 || labels[^1].Length < 2 || !char.IsAsciiLetter(labels[^1][0]) ||
            NonPublicSuffixes.Contains(labels[^1])) return null;
        foreach (var label in labels)
        {
            if (label.Length is 0 or > 63 || !char.IsAsciiLetterOrDigit(label[0]) ||
                !char.IsAsciiLetterOrDigit(label[^1]) ||
                label.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-')) return null;
        }
        return value.ToLowerInvariant();
    }

    internal static bool IsAllowedDestination(Uri uri) =>
        uri.IsAbsoluteUri && uri.AbsoluteUri.Length <= 2048 && uri.UserInfo.Length == 0 &&
        ((uri.Scheme == Uri.UriSchemeHttps && uri.Port == 443) || (uri.Scheme == Uri.UriSchemeHttp && uri.Port == 80)) &&
        uri.HostNameType == UriHostNameType.Dns && NormalizeHost(uri.IdnHost.TrimEnd('.')) is not null;

    internal static Uri? ResolveLink(Uri document, string? href)
    {
        if (string.IsNullOrWhiteSpace(href) || href.Length > 2048 || href.Contains('\\') || href.Any(char.IsControl) ||
            !Uri.TryCreate(document, href, out var target) || !IsAllowedDestination(target)) return null;
        return target.Fragment.Length == 0 ? target : new UriBuilder(target) { Fragment = string.Empty }.Uri;
    }

    internal static bool ArePublicAddresses(IReadOnlyList<IPAddress> addresses) =>
        addresses.Count is > 0 and <= 16 && addresses.All(IsPublicAddress);

    private static bool IsPublicAddress(IPAddress address)
    {
        Span<byte> bytes = stackalloc byte[16];
        if (!address.TryWriteBytes(bytes, out var length)) return false;
        if (address.AddressFamily == AddressFamily.InterNetwork && length == 4)
        {
            var a = bytes[0];
            var b = bytes[1];
            var c = bytes[2];
            // Reject non-global ranges in the IANA special-purpose registry, plus multicast and reserved space.
            return a is > 0 and < 224 and not (10 or 127) &&
                !(a == 100 && b is >= 64 and <= 127) &&
                // Azure exposes this host-local platform address through a globally assigned IP.
                !(a == 168 && b == 63 && c == 129 && bytes[3] == 16) &&
                !(a == 169 && b == 254) &&
                !(a == 172 && b is >= 16 and <= 31) &&
                !(a == 192 && ((b == 0 && c is 0 or 2) || (b == 88 && c == 99) || b == 168)) &&
                !(a == 198 && (b is 18 or 19 || (b == 51 && c == 100))) &&
                !(a == 203 && b == 0 && c == 113);
        }

        // Global unicast only. Exclude IPv4 mapping/translation, protocol assignments, 6to4 and documentation.
        return address.AddressFamily == AddressFamily.InterNetworkV6 && length == 16 && address.ScopeId == 0 &&
            (bytes[0] & 0xe0) == 0x20 &&
            !(bytes[0] == 0x20 && bytes[1] == 0x01 && (bytes[2] <= 1 || (bytes[2] == 0x0d && bytes[3] == 0xb8))) &&
            !(bytes[0] == 0x20 && bytes[1] == 0x02) &&
            !(bytes[0] == 0x3f && bytes[1] == 0xff && (bytes[2] & 0xf0) == 0);
    }

    internal static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        UseCookies = false,
        Credentials = null,
        ActivityHeadersPropagator = null,
        SslOptions = new SslClientAuthenticationOptions
        {
            // A publisher's certificate must not trigger connections outside the pinned transport.
            CertificateChainPolicy = new X509ChainPolicy
            {
                DisableCertificateDownloads = true,
                RevocationMode = X509RevocationMode.NoCheck,
                ApplicationPolicy = { new Oid("1.3.6.1.5.5.7.3.1") } // TLS server authentication
            }
        },
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        ConnectTimeout = TimeSpan.FromMilliseconds(1400),
        PooledConnectionLifetime = TimeSpan.Zero,
        MaxConnectionsPerServer = 4,
        MaxResponseHeadersLength = 16,
        MaxResponseDrainSize = 0,
        ConnectCallback = ConnectPublicAsync
    };

    private static async ValueTask<Stream> ConnectPublicAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var target = context.InitialRequestMessage.RequestUri;
        if (target is null || !IsAllowedDestination(target) || context.DnsEndPoint.Port != target.Port ||
            !string.Equals(context.DnsEndPoint.Host, target.IdnHost, StringComparison.OrdinalIgnoreCase))
            throw new HttpRequestException("Unsupported favicon destination.");

        // An absolute DNS name avoids local search suffixes. Dial the validated IP, not the DNS hostname.
        var addresses = await Dns.GetHostAddressesAsync(target.IdnHost.TrimEnd('.') + ".", ct);
        if (!ArePublicAddresses(addresses)) throw new HttpRequestException("Favicon destination is not public.");

        foreach (var address in addresses.OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1).Take(4))
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, target.Port), ct);
                // HttpClient still validates TLS and sends SNI using the original hostname.
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException) { socket.Dispose(); }
            catch { socket.Dispose(); throw; }
        }
        throw new HttpRequestException("No public favicon address accepted the connection.");
    }
}
