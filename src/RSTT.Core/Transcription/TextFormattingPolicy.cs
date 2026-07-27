using System.Text.RegularExpressions;

namespace RSTT.Core.Transcription;

public sealed partial class TextFormattingPolicy
{
    private readonly Regex _whitespace = Whitespace();
    private readonly Regex _spaceBeforeClosingPunctuation = SpaceBeforeClosingPunctuation();
    private readonly Regex _spaceAfterOpeningPunctuation = SpaceAfterOpeningPunctuation();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"\s+([,.;:!?%\)\]\}])")]
    private static partial Regex SpaceBeforeClosingPunctuation();

    [GeneratedRegex(@"([\(\[\{])\s+")]
    private static partial Regex SpaceAfterOpeningPunctuation();

    public string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var normalized = _whitespace.Replace(text.Trim(), " ");
        normalized = _spaceBeforeClosingPunctuation.Replace(normalized, "$1");
        return _spaceAfterOpeningPunctuation.Replace(normalized, "$1");
    }

    public string Append(string existing, string addition)
    {
        existing = Normalize(existing);
        addition = Normalize(addition);
        if (existing.Length == 0)
        {
            return addition;
        }

        if (addition.Length == 0)
        {
            return existing;
        }

        if (IsClosingPunctuation(addition[0]) || IsOpeningPunctuation(existing[^1]))
        {
            return existing + addition;
        }

        return existing + " " + addition;
    }

    private static bool IsClosingPunctuation(char value) => value is ',' or '.' or ';' or ':' or '!' or '?' or '%' or ')' or ']' or '}';

    private static bool IsOpeningPunctuation(char value) => value is '(' or '[' or '{';
}
