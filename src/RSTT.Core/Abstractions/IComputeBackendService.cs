using RSTT.Core.Models;

namespace RSTT.Core.Abstractions;

public interface IComputeBackendService
{
    Task<IReadOnlyList<ComputeBackendProbe>> ProbeAsync(
        CancellationToken cancellationToken = default);

    Task<ComputeSelectionResult> SelectAsync(
        ComputeBackend requested,
        ModelDescriptor model,
        CancellationToken cancellationToken = default);

    Task<ComputeDiagnosticsReport> GetDiagnosticsAsync(
        ComputeBackend requested,
        ModelDescriptor? model = null,
        CancellationToken cancellationToken = default);
}
