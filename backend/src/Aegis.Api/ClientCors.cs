namespace Aegis.Api;

public static class ClientCors
{
    // Additional development/LAN origins are explicit, never wildcard or credentials.
    public static string[] AdditionalOrigins(string? value) => (value ?? "")
        .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Select(origin =>
        {
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) ||
                (uri.Scheme != "http" && uri.Scheme != "https") ||
                !string.IsNullOrEmpty(uri.UserInfo) || uri.AbsolutePath != "/" ||
                !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || origin.Contains('*'))
                throw new InvalidOperationException("AEGIS_CORS_ORIGINS must contain explicit HTTP(S) origins separated by semicolons.");
            return uri.GetLeftPart(UriPartial.Authority);
        }).Distinct().ToArray();
}
