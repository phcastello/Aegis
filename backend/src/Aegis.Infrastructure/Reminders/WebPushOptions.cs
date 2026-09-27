using System.Security.Cryptography;

namespace Aegis.Infrastructure.Reminders;

public sealed class WebPushOptions
{
    public string Subject { get; set; } = "";
    public string PublicKey { get; set; } = "";
    public string PrivateKey { get; set; } = "";
    public int PollSeconds { get; set; } = 5;
    public bool IsConfigured => ValidKeys() && Uri.TryCreate(Subject, UriKind.Absolute, out var uri) && uri.Scheme is "mailto" or "https";
    private static byte[] Decode(string value) => Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/').PadRight((value.Length + 3) / 4 * 4, '='));
    private bool ValidKeys()
    {
        try
        {
            var pub = Decode(PublicKey);
            var privateKey = Decode(PrivateKey);
            if (pub.Length != 65 || pub[0] != 4 || privateKey.Length != 32) return false;
            using var ec = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, D = privateKey });
            var point = ec.ExportParameters(false).Q;
            return pub[1..33].SequenceEqual(point.X!) && pub[33..65].SequenceEqual(point.Y!);
        }
        catch (Exception e) when (e is FormatException or CryptographicException or ArgumentException) { return false; }
    }
}
