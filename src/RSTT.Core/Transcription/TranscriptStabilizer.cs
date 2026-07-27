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
    private string _confirmedText = string.Empty;

    public TranscriptStabilizer(TextFormattingPolicy formatting)
    {
        _formatting = formatting;
    }

    public TranscriptUpdate Process(RecognitionResult recognitionResult)
    {
        var current = _formatting.Normalize(recognitionResult.Text);
        var previousPending = GetUnconfirmedSuffix(_previousHypothesis);
        var currentPending = GetUnconfirmedSuffix(current);
        var newlyStable = recognitionResult.IsFinal
            ? currentPending
            : GetCommonCompletedPrefix(previousPending, currentPending);

        newlyStable = _formatting.Normalize(newlyStable);
        if (newlyStable.Length > 0)
        {
            _confirmedText = _formatting.Append(_confirmedText, newlyStable);
        }

        _previousHypothesis = current;
        var pending = GetUnconfirmedSuffix(current);
        return new TranscriptUpdate(_confirmedText, pending, newlyStable, recognitionResult.IsFinal);
    }

    public void Reset()
    {
        _previousHypothesis = string.Empty;
        _confirmedText = string.Empty;
    }

    private string GetUnconfirmedSuffix(string hypothesis)
    {
        if (_confirmedText.Length == 0)
        {
            return hypothesis;
        }

        if (hypothesis.StartsWith(_confirmedText, StringComparison.OrdinalIgnoreCase))
        {
            return hypothesis[_confirmedText.Length..].Trim();
        }

        // A recognizer can occasionally revise a previously confirmed word.  We cannot retract
        // text already typed into another application. Remove its shared prefix from the pending
        // display/output so a correction never repeats already confirmed words.
        var sharedPrefix = GetCommonCompletedPrefix(_confirmedText, hypothesis);
        return sharedPrefix.Length == 0 ? hypothesis : hypothesis[sharedPrefix.Length..].Trim();
    }

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
