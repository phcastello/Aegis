using System.Text.RegularExpressions;

namespace Aegis.Application.Memory;

// High-confidence authentication secret patterns only. Memory is not a password manager.
public static partial class MemorySecretGuard
{
    [GeneratedRegex(@"(?i)\b(?:minha\s+senha|meu\s+password|api[ _-]?key|chave\s+de\s+api|bearer\s+token|recovery\s+code|c[oó]digo\s+de\s+recupera[cç][aã]o|otp|session[ _-]?cookie)\b\s*(?:[=:]|\bé\b)\s*\S+|\b(?:sk-[A-Za-z0-9_-]{8,}|ghp_[A-Za-z0-9]{12,}|github_pat_[A-Za-z0-9_]{12,}|AIza[A-Za-z0-9_-]{20,})\b|-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----|\bBearer\s+[A-Za-z0-9._~+/=-]{12,}", RegexOptions.CultureInvariant)]
    private static partial Regex SecretPattern();

    public static bool ContainsSecret(string? content) => !string.IsNullOrEmpty(content) && SecretPattern().IsMatch(content);

    public static void RejectIfSecret(string content)
    {
        if (ContainsSecret(content))
            throw new MemoryException("memory_secret_not_allowed", "Segredos de autenticação não podem ser guardados na memória.");
    }
}
