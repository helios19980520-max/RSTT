namespace RSTT.Core.Models;

public sealed record RecognitionResult(
    string Text,
    bool IsFinal,
    long SequenceNumber,
    DateTimeOffset ProducedAt,
    float? Confidence = null,
    string Language = "en");
