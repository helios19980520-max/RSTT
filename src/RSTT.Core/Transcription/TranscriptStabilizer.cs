using RSTT.Core.Models;

namespace RSTT.Core.Transcription;

/// <summary>
/// Confirms only the common, completed prefix of consecutive ASR hypotheses.  Text that has
/// been emitted is never reconsidered: an engine may revise its suffix but cannot cause a
/// second injection of the confirmed prefix.
/// </summary>
public sealed class TranscriptStabilizer
{
    private readonly TextFormattingPolicy _formatting;
    private string _previousHypothesis = string.Empty;
    private string _segmentConfirmedText = string.Empty;
    private string _confirmedText = string.Empty;

    public TranscriptStabilizer(TextFormattingPolicy formatting)
    {
        _formatting = formatting;
    }

    public TranscriptUpdate Process(RecognitionResult recognitionResult)
    {
        var current = _formatting.Normalize(recognitionResult.Text);
        var stableCandidate = recognitionResult.IsFinal
            ? current
            : GetCommonCompletedPrefix(_previousHypothesis, current);
        var stableContent = GetUnconfirmedSegmentSuffix(stableCandidate);

        stableContent = _formatting.Normalize(stableContent);
        var appendDelta = string.Empty;
        if (stableContent.Length > 0)
        {
            var appendedText = _formatting.Append(_confirmedText, stableContent);
            appendDelta = appendedText[_confirmedText.Length..];
            _confirmedText = appendedText;
            _segmentConfirmedText = _formatting.Append(_segmentConfirmedText, stableContent);
        }

        _previousHypothesis = current;
        var pending = GetUnconfirmedSegmentSuffix(current);
        var update = new TranscriptUpdate(_confirmedText, pending, appendDelta, recognitionResult.IsFinal);
        if (recognitionResult.IsFinal)
        {
            ResetSegment();
        }

        return update;
    }

    public void Reset()
    {
        _previousHypothesis = string.Empty;
        _segmentConfirmedText = string.Empty;
        _confirmedText = string.Empty;
    }

    private string GetUnconfirmedSegmentSuffix(string hypothesis)
    {
        if (_segmentConfirmedText.Length == 0)
        {
            return hypothesis;
        }

        if (hypothesis.StartsWith(_segmentConfirmedText, StringComparison.OrdinalIgnoreCase))
        {
            return hypothesis[_segmentConfirmedText.Length..].Trim();
        }

        // A recognizer can revise a word that was already confirmed. We cannot
        // retract typed text, so anchor on the longest trailing word sequence
        // that still exists in the revised hypothesis and emit only what follows.
        return GetSuffixAfterWordOverlap(_segmentConfirmedText, hypothesis);
    }

    private void ResetSegment()
    {
        _previousHypothesis = string.Empty;
        _segmentConfirmedText = string.Empty;
    }

    private static string GetSuffixAfterWordOverlap(string committed, string hypothesis)
    {
        var committedWords = committed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var hypothesisWords = hypothesis.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var maximumOverlap = Math.Min(committedWords.Length, hypothesisWords.Length);
        for (var overlap = maximumOverlap; overlap >= 1; overlap--)
        {
            var committedStart = committedWords.Length - overlap;
            for (var hypothesisStart = 0; hypothesisStart + overlap <= hypothesisWords.Length; hypothesisStart++)
            {
                var matches = true;
                for (var index = 0; index < overlap; index++)
                {
                    if (!string.Equals(
                            NormalizeAnchorWord(committedWords[committedStart + index]),
                            NormalizeAnchorWord(hypothesisWords[hypothesisStart + index]),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        matches = false;
                        break;
                    }
                }

                if (matches)
                {
                    return string.Join(' ', hypothesisWords.Skip(hypothesisStart + overlap));
                }
            }
        }

        return string.Empty;
    }

    private static string NormalizeAnchorWord(string word) =>
        word.Trim(',', '.', ';', ':', '!', '?', '%', '(', ')', '[', ']', '{', '}', '"', '\'');

    private static string GetCommonCompletedPrefix(string previous, string current)
    {
        if (previous.Length == 0 || current.Length == 0)
        {
            return string.Empty;
        }

        var equalLength = 0;
        var compareLength = Math.Min(previous.Length, current.Length);
        while (equalLength < compareLength && char.ToUpperInvariant(previous[equalLength]) == char.ToUpperInvariant(current[equalLength]))
        {
            equalLength++;
        }

        if (equalLength == previous.Length && equalLength == current.Length)
        {
            return previous;
        }

        if (equalLength == previous.Length)
        {
            return previous;
        }

        if (equalLength == current.Length)
        {
            return string.Empty;
        }

        var boundary = -1;
        for (var index = 0; index < equalLength; index++)
        {
            if (char.IsWhiteSpace(previous[index]))
            {
                boundary = index;
            }
            else if (previous[index] is ',' or '.' or ';' or ':' or '!' or '?')
            {
                boundary = index + 1;
            }
        }

        return boundary <= 0 ? string.Empty : previous[..boundary].Trim();
    }
}
