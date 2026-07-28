namespace RSTT.Core.Models;

public enum RecognitionMode
{
    LowPower,
    Balanced,
    LowLatency,
    Accuracy,
}

/// <summary>Scheduling facts supplied by the model export rather than invented by the UI.</summary>
public sealed record StreamingRecognitionProfile(
    string Id,
    string DisplayName,
    int ChunkDurationMs,
    int LookaheadMs,
    int ExpectedLatencyMs,
    bool IsCacheAware,
    bool IsBuffered,
    int RecommendedThreads);
