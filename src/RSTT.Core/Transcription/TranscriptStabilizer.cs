using RSTT.Core.Models;

namespace RSTT.Core.Transcription;

/// <summary>
/// Normalizes engine hypotheses and delegates all commit decisions to one
/// generation-scoped policy.
/// </summary>
public sealed class TranscriptStabilizer
{
    private readonly TextFormattingPolicy _formatting;
    private readonly ITranscriptCommitPolicy _commitPolicy;
    private RecognitionHypothesis? _lastHypothesis;

    public TranscriptStabilizer(
        TextFormattingPolicy formatting,
        ITranscriptCommitPolicy? commitPolicy = null)
    {
        _formatting = formatting;
        _commitPolicy = commitPolicy ?? new StablePrefixCommitPolicy();
    }

    public TranscriptUpdate Process(RecognitionHypothesis hypothesis)
    {
        ArgumentNullException.ThrowIfNull(hypothesis);
        _lastHypothesis = hypothesis.IsFinal ? null : hypothesis;
        return _commitPolicy.Process(hypothesis, _formatting.Normalize(hypothesis.Text));
    }

    public void Reset()
    {
        _lastHypothesis = null;
        _commitPolicy.Reset();
    }

    // A manual paste freezes the visible hypothesis, but retains the stream's
    // prefix anchor so later revisions/finalization cannot emit it a second time.
    public TranscriptUpdate? CommitPending() => _lastHypothesis is { } hypothesis
        ? _commitPolicy.Process(hypothesis with { CommitPending = true }, _formatting.Normalize(hypothesis.Text))
        : null;

    /// <summary>
    /// Drops only revisable state after stream recovery while retaining already
    /// committed transcript history.
    /// </summary>
    public string ResetCurrentSegment()
    {
        _lastHypothesis = null;
        return _commitPolicy.ResetCurrentSegment();
    }
}
