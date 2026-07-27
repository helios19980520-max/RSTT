namespace RSTT.Core.Abstractions;

public interface ITextInjectionService
{
    Task<TextInjectionResult> InjectTextAsync(string text, CancellationToken cancellationToken = default);
}

public sealed record TextInjectionResult(bool Succeeded, string? Message = null);
