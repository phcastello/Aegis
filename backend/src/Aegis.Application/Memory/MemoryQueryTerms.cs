using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Aegis.Application.Memory;

public static class MemoryQueryTerms
{
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "A", "AS", "O", "OS", "DE", "DA", "DAS", "DO", "DOS", "E", "EU", "MEU", "MINHA",
        "MEUS", "MINHAS", "UM", "UMA", "QUE", "QUAL", "QUAIS", "COM", "PARA", "POR", "NO",
        "NA", "NOS", "NAS", "EM", "SE", "ELE", "ELA", "SER", "ERA", "FOI", "TEM", "HOJE",
        "MAIS", "SOBRE", "SABE", "SABER", "DEPOIS", "DIZIA", "GOSTO", "GOSTAR", "TIRANDO"
    };

    public static bool IsHistorical(string query) => Regex.IsMatch(Fold(query),
        @"\b(HISTORIC\w*|PASSADO|ANTIG\w*|TINHA|TEVE|JA FOI|ANTES)\b", RegexOptions.CultureInvariant);

    public static IReadOnlyList<string> SqlTokens(string query) => Regex.Matches(query, @"[\p{L}\p{N}]+")
        .Select(x => x.Value).Where(x => x.Length >= 2 && !StopWords.Contains(Fold(x)))
        .Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToArray();

    public static IReadOnlySet<string> Stems(string value) => Regex.Matches(value, @"[\p{L}\p{N}]+")
        .Select(x => Stem(Fold(x.Value))).Where(x => x.Length >= 2 && !StopWords.Contains(x))
        .ToHashSet(StringComparer.Ordinal);

    private static string Stem(string value) => value.Length > 5 && "AEIOU".Contains(value[^1]) ? value[..^1] : value;

    private static string Fold(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                builder.Append(char.ToUpperInvariant(ch));
        return builder.ToString();
    }
}
