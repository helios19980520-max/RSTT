namespace RSTT.Core.Models;

public sealed record CaptionSegment(
    long Id,
    string Text,
    bool IsFinal,
    DateTimeOffset Timestamp,
    long Sequence,
    SessionGenerationId SessionGenerationId = default);
