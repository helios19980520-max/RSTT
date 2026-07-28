namespace RSTT.Core.Models;

/// <summary>
/// Immutable text that may be delivered to the target application. Its text is
/// copied verbatim into the matching <see cref="InjectionRequest"/>.
/// </summary>
public sealed record TranscriptCommit(
    SessionGenerationId SessionGenerationId,
    long CommitId,
    long SourceSequenceId,
    string Text,
    bool IsEndpointFinal,
    DateTimeOffset Timestamp);
