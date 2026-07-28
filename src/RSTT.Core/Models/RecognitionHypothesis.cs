namespace RSTT.Core.Models;

/// <summary>A revision-capable result emitted by a speech engine.</summary>
public sealed record RecognitionHypothesis(
    SessionGenerationId SessionGenerationId,
    long SequenceId,
    string Text,
    bool IsFinal,
    DateTimeOffset Timestamp,
    string Language,
    string EngineId,
    float? Confidence = null);
