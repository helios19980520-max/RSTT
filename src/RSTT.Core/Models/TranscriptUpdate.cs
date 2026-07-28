namespace RSTT.Core.Models;

/// <summary>Caption state and the immutable commits produced by one hypothesis.</summary>
public sealed record TranscriptUpdate(
    TranscriptSnapshot Snapshot,
    IReadOnlyList<TranscriptCommit> Commits,
    bool IsFinal,
    string CurrentCaptionText = "",
    string? FinalizedSegmentText = null,
    long Sequence = 0,
    SessionGenerationId SessionGenerationId = default)
{
    public string StableText => Snapshot.ConfirmedHistory;

    public string PendingText => Snapshot.PendingCurrent;

    public string NewlyStableText => string.Concat(Commits.Select(static commit => commit.Text));
}
