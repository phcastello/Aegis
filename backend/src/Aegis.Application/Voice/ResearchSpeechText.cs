using System.Text.RegularExpressions;

namespace Aegis.Application.Voice;

public static class ResearchSpeechText
{
    private static readonly Regex InlineLink = new(@"\[([^\]\r\n]+)\]\([^\r\n)]*\)", RegexOptions.NonBacktracking);
    private static readonly Regex SourcesAndMarkup = new(
        @"\uE200[^\uE201]*\uE201|\[(?:\d+(?:\s*[-,]\s*\d+)*|\^[^\]\r\n]+)\]|(?:https?://|www\.)[^\s<>()\]]+|\*\*|__|\x60",
        RegexOptions.NonBacktracking | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex LineMarkup = new(@"^(?:#{1,6}|>|[-+*])[ \t]+");
    private static readonly Regex BeforePunctuation = new(@"[ \t]+([.,;:!?])");

    public static string FromAnswer(string answer)
    {
        // Only the spoken projection changes. The complete written text and its citation
        // positions are kept in the conversation.
        var withLabels = InlineLink.Replace(answer, match =>
        {
            var label = match.Groups[1].Value.Trim();
            return label.Equals("Referência", StringComparison.OrdinalIgnoreCase) ||
                label.Equals("Fonte", StringComparison.OrdinalIgnoreCase) || label.All(char.IsDigit) ? "" : label;
        });
        var spoken = new SpeechTextFilter().Append(SourcesAndMarkup.Replace(withLabels, ""));
        spoken = BeforePunctuation.Replace(spoken, "$1");
        var parts = spoken.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.Any(char.IsLetterOrDigit))
            .Select(line => line.StartsWith('|')
                ? string.Join(", ", line.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                : LineMarkup.Replace(line, ""));
        var text = string.Join('\n', parts);
        return text.Length > 0 ? text : "Terminei a pesquisa, mas não consegui preparar o áudio da resposta.";
    }
}
