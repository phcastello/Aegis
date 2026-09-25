using System.Security.Cryptography;
using System.Text;
using Aegis.Application.Voice.Transcription;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aegis.Infrastructure.Voice.Transcription;

public interface ISttKeytermProvider
{
    IReadOnlyList<string> GetKeyterms();
    string Version { get; }
}

public sealed class SttKeytermProvider : ISttKeytermProvider
{
    private const int HardMaximum = 100;
    private static readonly char[] ForbiddenCharacters = ['<', '>', '{', '}', '[', ']', '\\'];
    private readonly IReadOnlyList<string> keyterms;

    public SttKeytermProvider(IOptions<SttOptions> options, ILogger<SttKeytermProvider> logger)
    {
        var configuredLimit = Math.Clamp(options.Value.MaxKeyterms, 0, HardMaximum);
        var accepted = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawTerm in options.Value.Keyterms.Split(';', StringSplitOptions.None))
        {
            var term = string.Join(' ', rawTerm.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (string.IsNullOrWhiteSpace(term))
            {
                continue;
            }

            var invalidReason = GetInvalidReason(term);
            if (invalidReason is not null)
            {
                logger.LogWarning("{Event} {Reason}", "aegis_stt_keyterm_ignored", invalidReason);
                continue;
            }

            if (!seen.Add(term))
            {
                continue;
            }

            if (accepted.Count >= configuredLimit)
            {
                continue;
            }

            accepted.Add(term);
        }

        keyterms = accepted;
        Version = ComputeVersion(keyterms);
        logger.LogInformation("{Event} {KeytermCount} {KeytermSetVersion}", "aegis_stt_keyterms_ready", keyterms.Count, Version);
    }

    public string Version { get; }

    public IReadOnlyList<string> GetKeyterms() => keyterms;

    private static string? GetInvalidReason(string term)
    {
        if (term.Length >= 50) return "length";
        if (term.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 5) return "word_count";
        return term.IndexOfAny(ForbiddenCharacters) >= 0 ? "forbidden_character" : null;
    }

    private static string ComputeVersion(IEnumerable<string> terms)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', terms)));
        return Convert.ToHexString(bytes)[..12].ToLowerInvariant();
    }
}
