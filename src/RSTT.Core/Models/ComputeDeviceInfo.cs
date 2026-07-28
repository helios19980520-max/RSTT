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
    bool IsIntegrated,
    uint VendorId = 0,
    uint DeviceId = 0,
    bool IsSoftwareAdapter = false);

public enum ComputeReadinessLayer
{
    SystemRuntime,
    Hardware,
    Driver,
    CudaRuntime,
    Cudnn,
    SherpaCudaRuntime,
    WhisperCudaRuntime,
    ProviderLoad,
    ModelCompatibility,
    RecognizerLoad,
    Warmup,
    ActiveInference,
}

public enum ComputeLayerState
{
    Ready,
    Active,
    Missing,
    Incompatible,
    Failed,
    NotTested,
}

public sealed record ComputeLayerStatus(
    ComputeReadinessLayer Layer,
    bool IsReady,
    string Status,
    string Version = "",
    ComputeLayerState State = ComputeLayerState.NotTested,
    string RequiredVersion = "",
    string DetectedPath = "",
    string RemediationUrl = "");

public sealed record HardwareDetectionReport(
    IReadOnlyList<ComputeDeviceInfo> Adapters,
    string Source,
    DateTimeOffset DetectedAt);

public sealed record ComputeDiagnosticsReport(
    IReadOnlyList<ComputeLayerStatus> Layers,
    ComputeBackend RequestedBackend,
    ComputeBackend SelectedBackend,
    string ActiveBackendLabel);

public sealed record ComputeBenchmarkResult(
    string ModelId,
    string Revision,
    string ProfileId,
    ComputeBackend Backend,
    double RealtimeFactor,
    long PeakWorkingSetBytes,
    DateTimeOffset MeasuredAt,
    string RuntimeVersion);

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

public sealed record ComputeRuntimeEvidence(
    ComputeBackend Backend,
    ComputeReadinessLayer Layer,
    ComputeLayerState State,
    string Status,
    string ModelId = "",
    string RuntimeVersion = "",
    string DetectedPath = "",
    DateTimeOffset? ObservedAt = null)
{
    public bool IsReady =>
        State is ComputeLayerState.Ready or ComputeLayerState.Active;
}
