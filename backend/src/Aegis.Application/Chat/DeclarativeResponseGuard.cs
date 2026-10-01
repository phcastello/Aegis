using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Aegis.Application.Chat;

// A narrow final-response guard for obvious restatements of a factual declaration.
// It leaves useful comments and action replies intact. Streaming declarations are
// buffered until this check runs so the saved message matches what the user sees.
public static class DeclarativeResponseGuard
{
    private static readonly HashSet<string> Filler = new(StringComparer.Ordinal)
    {
        "A", "AO", "AS", "O", "OS", "DE", "DA", "DAS", "DO", "DOS", "E", "EU", "MEU",
        "MINHA", "SEU", "SUA", "ELE", "ELA", "ISSO", "ESSA", "ESSE", "QUE", "COM", "EM",
        "NO", "NA", "PARA", "POR", "UM", "UMA", "SER", "ESTA", "ESTAO", "FOI", "TEM",
        "CERTO", "ENTENDI", "OK", "TA", "INDICA", "SIGNIFICA", "QUER", "DIZER", "PEDRO", "AGORA",
        "MEMORIA"
    };

    public static bool ShouldReview(string userContent)
    {
        if (string.IsNullOrWhiteSpace(userContent) || userContent.Contains('?')) return false;
        return !Regex.IsMatch(Fold(userContent),
            @"^\s*(LEMBR\w*|GUARD\w*|ESQUEC\w*|APAG\w*|REMOV\w*|CORRIJ\w*|ATUALIZ\w*|REGISTR\w*|ANOT\w*|FA(CA|Z)|CRIE|CRIAR|ENVIE|MAND\w*|EXPLIQU\w*|MOSTR\w*|DIGA|BUSQU\w*|CONSULT\w*)\b",
            RegexOptions.CultureInvariant);
    }

    public static string Normalize(string userContent, string assistantContent)
    {
        if (!ShouldReview(userContent) || string.IsNullOrWhiteSpace(assistantContent) ||
            assistantContent.Contains('?') || assistantContent.Length > 240) return assistantContent;
        var userTerms = Terms(userContent);
        var answerTerms = Terms(assistantContent);
        if (answerTerms.Count < 2 || answerTerms.Except(userTerms).Any()) return assistantContent;
        var acknowledgement = Regex.Match(assistantContent.TrimStart(),
            @"^(Certo|Entendi|Ok|Tá)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return acknowledgement.Success ? acknowledgement.Groups[1].Value.TrimEnd('.') + "." : "Certo.";
    }

    private static HashSet<string> Terms(string text) => Regex.Matches(Fold(text), @"[A-Z0-9]+")
        .Select(x => x.Value).Where(x => x.Length >= 2 && !Filler.Contains(x))
        .ToHashSet(StringComparer.Ordinal);

    private static string Fold(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                builder.Append(char.ToUpperInvariant(ch));
        return builder.ToString();
    }
}
