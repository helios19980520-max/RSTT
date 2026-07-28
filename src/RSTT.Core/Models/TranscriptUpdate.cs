namespace RSTT.Core.Models;

/// <summary>Caption state plus only the text that has newly become safe to inject.</summary>
public sealed record TranscriptUpdate(
    string StableText,
    string PendingText,
    string NewlyStableText,
    bool IsFinal,
    string CurrentCaptionText = "",
    string? FinalizedSegmentText = null,
    long Sequence = 0);
