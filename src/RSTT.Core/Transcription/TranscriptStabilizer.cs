using RSTT.Core.Models;

namespace RSTT.Core.Transcription;

/// <summary>
/// Confirms only the common, completed prefix of consecutive ASR hypotheses.  Text that has
/// been emitted is never reconsidered: an engine may revise its suffix but cannot cause a
/// second injection of the confirmed prefix.
/// </summary>
public sealed class TranscriptStabilizer
{
    private const int MaximumTranscriptCharacters = 6_000;
    private readonly TextFormattingPolicy _formatting;
    private readonly ITranscriptCommitPolicy _commitPolicy;
    private string _previousHypothesis = string.Empty;
    private string _segmentConfirmedText = string.Empty;
    private string _confirmedText = string.Empty;

    public TranscriptStabilizer(
        TextFormattingPolicy formatting,
        ITranscriptCommitPolicy? commitPolicy = null)
    {
        _formatting = formatting;
        _commitPolicy = commitPolicy ?? new StablePrefixCommitPolicy();
    }

    public TranscriptUpdate Process(RecognitionResult recognitionResult)
    {
        var current = _formatting.Normalize(recognitionResult.Text);
        var stableCandidate = _commitPolicy.GetStablePrefix(
            _previousHypothesis,
            current,
            recognitionResult.IsFinal);
        var stableContent = GetUnconfirmedSegmentSuffix(stableCandidate);

        stableContent = _formatting.Normalize(stableContent);
        var appendDelta = string.Empty;
        if (stableContent.Length > 0)
        {
            var appendedText = _formatting.Append(_confirmedText, stableContent);
            appendDelta = appendedText[_confirmedText.Length..];
            _confirmedText = BoundTranscript(appendedText);
            _segmentConfirmedText = _formatting.Append(_segmentConfirmedText, stableContent);
        }

        _previousHypothesis = current;
        var pending = GetUnconfirmedSegmentSuffix(current);
        var update = new TranscriptUpdate(
            _confirmedText,
            pending,
            appendDelta,
            recognitionResult.IsFinal,
            current,
            recognitionResult.IsFinal ? current : null,
            recognitionResult.SequenceNumber);
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

    /// <summary>
    /// Drops only the revisable hypothesis state after a recognizer-stream recovery while
    /// preserving transcript text already committed during the current session.
    /// </summary>
    public string ResetCurrentSegment()
    {
        _previousHypothesis = string.Empty;
        _segmentConfirmedText = string.Empty;
        return _confirmedText;
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

    private static string BoundTranscript(string text)
    {
        if (text.Length <= MaximumTranscriptCharacters)
        {
            return text;
        }

        var minimumStart = text.Length - MaximumTranscriptCharacters;
        var wordBoundary = text.IndexOf(' ', minimumStart);
        return wordBoundary >= 0 && wordBoundary < text.Length - 1
            ? text[(wordBoundary + 1)..]
            : text[^MaximumTranscriptCharacters..];
    }
}
