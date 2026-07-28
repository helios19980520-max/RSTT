namespace RSTT.Core.Models;

/// <summary>The three independently meaningful views of the current transcript.</summary>
public sealed record TranscriptSnapshot(
    string ConfirmedHistory,
    string StableCurrent,
    string PendingCurrent);
