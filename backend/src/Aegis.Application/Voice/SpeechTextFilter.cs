using System.Text;

namespace Aegis.Application.Voice;

/// <summary>Projects the words to synthesize without changing the written answer.</summary>
internal sealed class SpeechTextFilter
{
    // A parenthetical may start in one committed speech unit and end in another.
    private int parenthesisDepth;

    public string Append(string text)
    {
        if (parenthesisDepth == 0 && text.AsSpan().IndexOfAny('(', '（') < 0)
            return text;

        var spoken = new StringBuilder(text.Length);
        var afterParenthetical = false;
        foreach (var character in text)
        {
            if (character is '(' or '（')
            {
                parenthesisDepth++;
                continue;
            }

            if (parenthesisDepth > 0)
            {
                if (character is ')' or '）')
                    afterParenthetical = --parenthesisDepth == 0;
                continue;
            }

            if (afterParenthetical)
            {
                if (char.IsWhiteSpace(character) &&
                    (spoken.Length == 0 || char.IsWhiteSpace(spoken[^1])))
                    continue;

                if (!char.IsWhiteSpace(character))
                {
                    if (character is '.' or ',' or ';' or ':' or '!' or '?')
                    {
                        while (spoken.Length > 0 && char.IsWhiteSpace(spoken[^1]))
                            spoken.Length--;
                    }
                    else if (spoken.Length > 0 && char.IsLetterOrDigit(spoken[^1]) &&
                             char.IsLetterOrDigit(character))
                    {
                        spoken.Append(' ');
                    }
                    afterParenthetical = false;
                }
            }

            spoken.Append(character);
        }

        var result = spoken.ToString().Trim();
        return result.Any(char.IsLetterOrDigit) ? result : string.Empty;
    }
}
