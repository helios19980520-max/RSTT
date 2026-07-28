using RSTT.Core.Models;

namespace RSTT.Core.Abstractions;

public interface IComputeBenchmarkService
{
    Task<ComputeBenchmarkResult> MeasureAsync(
        ModelInstallation installation,
        ComputeBackend backend,
        ReadOnlyMemory<float> audio,
        CancellationToken cancellationToken = default);
}
