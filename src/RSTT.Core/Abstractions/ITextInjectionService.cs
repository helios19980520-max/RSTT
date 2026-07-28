namespace RSTT.Core.Abstractions;

public interface ITextInjectionService
{
    Task<TextInjectionResult> InjectTextAsync(string text, CancellationToken cancellationToken = default);
}

public enum TextInjectionStatus
{
    Success,
    SelfFocused,
    TargetChanged,
    ElevatedTarget,
    NoTarget,
    Failed,
}

public sealed record TextInjectionResult(
    bool Succeeded,
    string? Message = null,
    TextInjectionStatus Status = TextInjectionStatus.Success);
