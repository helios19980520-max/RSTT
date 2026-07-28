using RSTT.Core.Models;

namespace RSTT.Core.Compute;

public static class ComputeSelectionPolicy
{
    public static ComputeSelectionResult Select(
        ComputeBackend requested,
        ModelDescriptor model,
        IReadOnlyList<ComputeBackendProbe> probes)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(probes);

        var cpu = probes.FirstOrDefault(probe => probe.Backend == ComputeBackend.Cpu);
        var cuda = probes.FirstOrDefault(probe => probe.Backend == ComputeBackend.Cuda);

        if (requested == ComputeBackend.Cpu)
        {
            return new ComputeSelectionResult(
                requested,
                ComputeBackend.Cpu,
                false,
                cpu?.Status ?? "CPU inference selected.",
                cpu?.Device);
        }

        if (requested == ComputeBackend.Cuda)
        {
            if (model.CudaSupported && cuda?.IsAvailable == true)
            {
                return new ComputeSelectionResult(requested, ComputeBackend.Cuda, false, cuda.Status, cuda.Device);
            }

            return new ComputeSelectionResult(
                requested,
                ComputeBackend.Cpu,
                true,
                cuda?.Status ?? "CUDA was requested but no validated CUDA runtime is available.",
                cpu?.Device);
        }

        if (model.CudaSupported && cuda?.IsAvailable == true)
        {
            return new ComputeSelectionResult(requested, ComputeBackend.Cuda, false, cuda.Status, cuda.Device);
        }

        return new ComputeSelectionResult(
            requested,
            ComputeBackend.Cpu,
            cuda is not null,
            cuda?.Status ?? cpu?.Status ?? "CPU inference selected.",
            cpu?.Device);
    }
}
