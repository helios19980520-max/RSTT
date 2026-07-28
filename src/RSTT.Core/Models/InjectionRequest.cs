namespace RSTT.Core.Models;

/// <summary>An ordered, immutable request to inject exactly one transcript commit.</summary>
public sealed record InjectionRequest(
    SessionGenerationId SessionGenerationId,
    long CommitId,
    long SequenceId,
    string Text,
    DateTimeOffset CreatedAt);
