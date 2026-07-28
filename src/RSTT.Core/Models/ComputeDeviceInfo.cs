namespace RSTT.Core.Models;

public enum ComputeBackend
{
    Auto,
    Cpu,
    Cuda,
}

public sealed record ComputeDeviceInfo(
    string Name,
    string Vendor,
    long DedicatedMemory,
    long SharedMemory,
    string DriverVersion,
    IReadOnlyList<ComputeBackend> BackendCapabilities,
    bool IsDiscrete,
    bool IsIntegrated);

public sealed record ComputeBackendProbe(
    ComputeBackend Backend,
    bool IsAvailable,
    string Status,
    ComputeDeviceInfo? Device = null);

public sealed record ComputeSelectionResult(
    ComputeBackend Requested,
    ComputeBackend Selected,
    bool FellBack,
    string Reason,
    ComputeDeviceInfo? Device = null);
