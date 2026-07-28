using RSTT.Core.Models;

namespace RSTT.Core.Transcription;

/// <summary>
/// Owns the revision-sensitive state for one transcript generation and produces
/// only immutable commits. Consumers must never infer injectable text from the
/// snapshot or from a raw hypothesis.
/// </summary>
public interface ITranscriptCommitPolicy
{
    TranscriptUpdate Process(RecognitionHypothesis hypothesis, string normalizedText);

    void Reset();

    string ResetCurrentSegment();
}

public abstract class TranscriptCommitPolicyBase : ITranscriptCommitPolicy
{
    private const int MaximumTranscriptCharacters = 6_000;
    private readonly TextFormattingPolicy _formatting = new();
    private string _previousHypothesis = string.Empty;
    private string _segmentConfirmedText = string.Empty;
    private string _confirmedHistory = string.Empty;
    private long _nextCommitId;

    public TranscriptUpdate Process(RecognitionHypothesis hypothesis, string normalizedText)
    {
        ArgumentNullException.ThrowIfNull(hypothesis);
        normalizedText ??= string.Empty;

        var stableCandidate = SelectStableCandidate(
            _previousHypothesis,
            normalizedText,
            hypothesis.IsFinal);
        var stableContent = _formatting.Normalize(GetUnconfirmedSegmentSuffix(stableCandidate));
        var commits = new List<TranscriptCommit>(1);

        if (stableContent.Length > 0)
        {
            var appendedText = _formatting.Append(_confirmedHistory, stableContent);
            var appendDelta = appendedText[_confirmedHistory.Length..];
            _confirmedHistory = BoundTranscript(appendedText);
            _segmentConfirmedText = _formatting.Append(_segmentConfirmedText, stableContent);

            if (appendDelta.Length > 0)
            {
                commits.Add(new TranscriptCommit(
                    hypothesis.SessionGenerationId,
                    ++_nextCommitId,
                    hypothesis.SequenceId,
                    appendDelta,
                    hypothesis.IsFinal,
                    hypothesis.Timestamp));
            }
        }

        _previousHypothesis = normalizedText;
        var pending = GetUnconfirmedSegmentSuffix(normalizedText);
        var snapshot = new TranscriptSnapshot(
            _confirmedHistory,
            _segmentConfirmedText,
            pending);
        var update = new TranscriptUpdate(
            snapshot,
            commits,
            hypothesis.IsFinal,
            normalizedText,
            hypothesis.IsFinal ? normalizedText : null,
            hypothesis.SequenceId,
            hypothesis.SessionGenerationId);

        if (hypothesis.IsFinal)
        {
            ResetSegment();
        }

        return update;
    }

    public void Reset()
    {
        _previousHypothesis = string.Empty;
        _segmentConfirmedText = string.Empty;
        _confirmedHistory = string.Empty;
        _nextCommitId = 0;
    }

    public string ResetCurrentSegment()
    {
        ResetSegment();
        return _confirmedHistory;
    }

    protected abstract string SelectStableCandidate(
        string previousHypothesis,
        string currentHypothesis,
        bool isFinal);

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

        // Committed text cannot be retracted. Anchor on the longest trailing word
        // sequence that remains in the revision and emit only what follows it.
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

/// <summary>For VAD/offline engines: only a complete segment may become a commit.</summary>
public sealed class FinalOnlyCommitPolicy : TranscriptCommitPolicyBase
{
    protected override string SelectStableCandidate(
        string previousHypothesis,
        string currentHypothesis,
        bool isFinal) =>
        isFinal ? currentHypothesis : string.Empty;
}

/// <summary>
/// For native streaming engines: requires two matching hypotheses, commits whole
/// words only, and retains the newest two words until a later hypothesis or final.
/// </summary>
public sealed class StablePrefixCommitPolicy : TranscriptCommitPolicyBase
{
    private const int HeldBackWordCount = 2;

    protected override string SelectStableCandidate(
        string previousHypothesis,
        string currentHypothesis,
        bool isFinal)
    {
        if (isFinal)
        {
            return currentHypothesis;
        }

        if (previousHypothesis.Length == 0 || currentHypothesis.Length == 0)
        {
            return string.Empty;
        }

        var equalLength = 0;
        var compareLength = Math.Min(previousHypothesis.Length, currentHypothesis.Length);
        while (equalLength < compareLength &&
               char.ToUpperInvariant(previousHypothesis[equalLength]) ==
               char.ToUpperInvariant(currentHypothesis[equalLength]))
        {
            equalLength++;
        }

        if (equalLength == 0)
        {
            return string.Empty;
        }

        var commonPrefix = previousHypothesis[..equalLength];
        if (equalLength < previousHypothesis.Length &&
            equalLength < currentHypothesis.Length &&
            commonPrefix.Length > 0 &&
            !char.IsWhiteSpace(commonPrefix[^1]) &&
            !IsWordTerminator(commonPrefix[^1]))
        {
            var lastBoundary = commonPrefix.LastIndexOf(' ');
            commonPrefix = lastBoundary < 0 ? string.Empty : commonPrefix[..lastBoundary];
        }

        var words = commonPrefix.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var committableWordCount = words.Length - HeldBackWordCount;
        if (committableWordCount <= 0)
        {
            return string.Empty;
        }

        return string.Join(' ', words.Take(committableWordCount));
    }

    private static bool IsWordTerminator(char value) =>
        value is ',' or '.' or ';' or ':' or '!' or '?' or ')' or ']' or '}';
}
