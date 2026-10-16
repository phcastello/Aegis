using System.Security.Cryptography;
using System.Text;
namespace Aegis.Infrastructure.Nodes;

public static class NodeSecrets
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    public static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));
    public static bool Matches(byte[] hash, string value) => CryptographicOperations.FixedTimeEquals(hash, Hash(value));
    public static string NewCode() => string.Concat(RandomNumberGenerator.GetBytes(24).Select(b => Alphabet[b & 31]));
    public static string DisplayCode(string code) => string.Join('-', Enumerable.Range(0, 6).Select(i => code.Substring(i * 4, 4)));
    public static string NormalizeCode(string code)
    {
        var normalized = (code ?? "").Replace("-", "").Replace(" ", "").ToUpperInvariant();
        if (normalized.Length != 24 || normalized.Any(c => !Alphabet.Contains(c))) throw PairingError();
        return normalized;
    }
    public static string Encode(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static byte[] RecoveryKey(string value)
    {
        if (value is null || value.Length != 43) throw PairingError();
        try {
            var bytes = Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + "=");
            if (bytes.Length != 32 || Encode(bytes) != value) throw PairingError();
            return bytes;
        } catch (FormatException) { throw PairingError(); }
    }
    public static string NewCredential(Guid nodeId) => $"aegis-node-v1.{nodeId:N}.{Encode(RandomNumberGenerator.GetBytes(32))}";
    public static Guid? CredentialNodeId(string credential)
    {
        if (credential is null || credential.Length > 128) return null;
        var parts = credential.Split('.');
        if (parts.Length != 3 || parts[0] != "aegis-node-v1" || !Guid.TryParseExact(parts[1], "N", out var id) || id == Guid.Empty) return null;
        try { RecoveryKey(parts[2]); return id; } catch (Aegis.Application.Nodes.NodeException) { return null; }
    }
    // A transient retry receipt; the encryption key belongs only to the native client.
    // The permanent credential table contains only SHA-256, never this ciphertext.
    public static byte[] EncryptReceipt(string credential, byte[] key, Guid attempt)
    {
        var nonce = RandomNumberGenerator.GetBytes(12); var plain = Encoding.UTF8.GetBytes(credential);
        var cipher = new byte[plain.Length]; var tag = new byte[16];
        using var aes = new AesGcm(key, 16); aes.Encrypt(nonce, plain, cipher, tag, attempt.ToByteArray());
        CryptographicOperations.ZeroMemory(plain);
        return [.. nonce, .. tag, .. cipher];
    }
    public static string DecryptReceipt(byte[] receipt, byte[] key, Guid attempt)
    {
        if (receipt.Length < 29) throw PairingError();
        try {
            var plain = new byte[receipt.Length - 28]; using var aes = new AesGcm(key, 16);
            aes.Decrypt(receipt.AsSpan(0, 12), receipt.AsSpan(28), receipt.AsSpan(12, 16), plain, attempt.ToByteArray());
            var value = Encoding.UTF8.GetString(plain); CryptographicOperations.ZeroMemory(plain); return value;
        } catch (CryptographicException) { throw PairingError(); }
    }
    public static Aegis.Application.Nodes.NodeException PairingError() => new("pairing_unavailable", "Código inválido, expirado ou já utilizado. Solicite um novo código se necessário.");
}
