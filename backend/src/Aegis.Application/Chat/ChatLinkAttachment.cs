using System.Text;
using Aegis.Domain.Entities;

namespace Aegis.Application.Chat;

public static class ChatLinkAttachment
{
    public const string ContentType = "text/uri-list";
    public const int MaximumUrlLength = 2048;

    public static ChatAttachment Create(Guid messageId, int position, string? value)
    {
        if (!TryParse(value, out var uri))
            throw new ArgumentException("Informe um link HTTP ou HTTPS válido de até 2.048 caracteres, sem credenciais.");
        return new ChatAttachment(messageId, position, uri.IdnHost, ContentType, Encoding.UTF8.GetBytes(uri.AbsoluteUri));
    }

    public static string? ReadUrl(byte[]? data) =>
        data is { Length: > 0 and <= MaximumUrlLength } && TryParse(Encoding.UTF8.GetString(data), out var uri)
            ? uri.AbsoluteUri : null;

    internal static string? PublicLookupUrl(string? url)
    {
        if (!TryParse(url, out var uri) || !uri.IsDefaultPort || uri.IsLoopback || uri.HostNameType != UriHostNameType.Dns)
            return null;
        var host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
        // Public lookup is delegated to the hosted search provider. Do not resolve a host
        // from the private Aegis server or expose a query string that may grant access.
        var suffix = host.Split('.').Last();
        if (!host.Contains('.') || host == "ts.net" || host.EndsWith(".ts.net", StringComparison.Ordinal) ||
            suffix is "localhost" or "local" or "localdomain" or "internal" or "lan" or "home" or "intranet" or "test"
                or "invalid" or "onion" or "arpa")
            return null;
        return uri.GetLeftPart(UriPartial.Path);
    }

    private static bool TryParse(string? value, out Uri uri)
    {
        uri = null!;
        if (value is null) return false;
        var text = value.Trim();
        if (text.Length is 0 or > MaximumUrlLength || text.Any(char.IsWhiteSpace) || text.Any(char.IsControl) ||
            text.Contains('\\') || !Uri.TryCreate(text, UriKind.Absolute, out var candidate) ||
            candidate is not { Scheme: "http" or "https", UserInfo: "", Host.Length: > 0 } ||
            candidate.IdnHost.Length > 253 ||
            Encoding.UTF8.GetByteCount(candidate.AbsoluteUri) > MaximumUrlLength)
            return false;
        uri = candidate;
        return true;
    }
}
