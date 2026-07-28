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
        return _commitPolicy.Process(hypothesis, _formatting.Normalize(hypothesis.Text));
    }

    public void Reset() => _commitPolicy.Reset();

    /// <summary>
    /// Drops only revisable state after stream recovery while retaining already
    /// committed transcript history.
    /// </summary>
    public string ResetCurrentSegment() => _commitPolicy.ResetCurrentSegment();
}
