using RSTT.Core.Models;

namespace RSTT.Core.Abstractions;

public interface ITextInjectionService
{
    Task<TextInjectionResult> InjectAsync(
        InjectionRequest request,
        CancellationToken cancellationToken = default);
}

public enum TextInjectionStatus
{
    Success,
    Partial,
    TargetChanged,
    SelfFocused,
    ElevatedTarget,
    Unavailable,
    Failed,
}

public sealed record TextInjectionResult(
    TextInjectionStatus Status,
    int ExpectedInputCount,
    int SentInputCount,
    int CommittedUtf16Offset,
    nint TargetWindowHandle,
    uint TargetProcessId,
    int Win32Error,
    string? DiagnosticMessage = null,
    int Utf16UnitsPerBlock = 0,
    int SendInputCallCount = 0,
    int QueueDepthAtEnqueue = 0,
    string DeliveryProfile = "")
{
    public bool Succeeded => Status == TextInjectionStatus.Success;
}
