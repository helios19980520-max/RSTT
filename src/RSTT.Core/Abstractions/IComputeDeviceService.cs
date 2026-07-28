using RSTT.Core.Models;

namespace RSTT.Core.Abstractions;

public interface IComputeDeviceService
{
    Task<IReadOnlyList<ComputeBackendProbe>> ProbeAsync(CancellationToken cancellationToken = default);

    Task<ComputeSelectionResult> SelectAsync(
        ComputeBackend requested,
        ModelDescriptor model,
        CancellationToken cancellationToken = default);
}
