using System.Runtime.InteropServices;
using RSTT.Core.Abstractions;
using RSTT.Core.Compute;
using RSTT.Core.Models;

namespace RSTT.Infrastructure;

/// <summary>
/// Reports hardware and each runtime readiness layer independently. Adapter
/// presence alone never makes CUDA selectable.
/// </summary>
public sealed class WindowsComputeDeviceService : IComputeDeviceService
{
    private readonly IHardwareDetectionService _hardware;
    private IReadOnlyList<ComputeBackendProbe>? _cached;

    public WindowsComputeDeviceService(IHardwareDetectionService hardware)
    {
        _hardware = hardware;
    }

    public async Task<IReadOnlyList<ComputeBackendProbe>> ProbeAsync(
        CancellationToken cancellationToken = default)
    {
        if (_cached is not null)
        {
            return _cached;
        }

        var hardware = await _hardware.DetectAsync(cancellationToken).ConfigureAwait(false);
        var cpu = new ComputeDeviceInfo(
            Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "Windows CPU",
            "CPU",
            0,
            0,
            string.Empty,
            [ComputeBackend.Cpu],
            false,
            true);
        var probes = new List<ComputeBackendProbe>
        {
            new(ComputeBackend.Cpu, true, "CPU sherpa runtime is available.", cpu),
        };
        var nvidia = hardware.Adapters.FirstOrDefault(adapter =>
            adapter.Vendor.Equals("NVIDIA", StringComparison.OrdinalIgnoreCase));
        if (nvidia is null)
        {
            probes.Add(new ComputeBackendProbe(
                ComputeBackend.Cuda,
                false,
                "No physical NVIDIA adapter was detected through DXGI."));
            return _cached = probes;
        }

        var layers = ProbeCudaRuntimeLayers(nvidia);
        var dependenciesPresent = layers
            .Where(layer => layer.Layer is ComputeReadinessLayer.CudaRuntime or
                ComputeReadinessLayer.Cudnn or
                ComputeReadinessLayer.SherpaCudaRuntime or
                ComputeReadinessLayer.ProviderLoad)
            .All(layer => layer.IsReady);
        var firstMissing = layers.FirstOrDefault(layer => !layer.IsReady);
        probes.Add(new ComputeBackendProbe(
            ComputeBackend.Cuda,
            false,
            dependenciesPresent
                ? "CUDA dependencies are present, but recognizer load, warmup, and decode have not been verified; CUDA remains unavailable."
                : firstMissing?.Status ?? "CUDA worker dependencies are incomplete.",
            nvidia));
        return _cached = probes;
    }

    public async Task<ComputeSelectionResult> SelectAsync(
        ComputeBackend requested,
        ModelDescriptor model,
        CancellationToken cancellationToken = default)
    {
        var probes = await ProbeAsync(cancellationToken).ConfigureAwait(false);
        return ComputeSelectionPolicy.Select(requested, model, probes);
    }

    public async Task<ComputeDiagnosticsReport> GetDiagnosticsAsync(
        ComputeBackend requested,
        ModelDescriptor? model = null,
        CancellationToken cancellationToken = default)
    {
        var hardware = await _hardware.DetectAsync(cancellationToken).ConfigureAwait(false);
        var nvidia = hardware.Adapters.FirstOrDefault(adapter =>
            adapter.Vendor.Equals("NVIDIA", StringComparison.OrdinalIgnoreCase));
        var layers = nvidia is null
            ? new List<ComputeLayerStatus>
            {
                new(
                    ComputeReadinessLayer.Hardware,
                    false,
                    "No physical NVIDIA adapter was detected through DXGI."),
            }
            : ProbeCudaRuntimeLayers(nvidia);
        if (model is not null)
        {
            layers.Add(new ComputeLayerStatus(
                ComputeReadinessLayer.ModelCompatibility,
                model.CudaSupported,
                model.CudaSupported
                    ? $"{model.DisplayName} declares CUDA compatibility."
                    : $"{model.DisplayName} is CPU-only."));
        }

        var selection = model is null
            ? new ComputeSelectionResult(
                requested,
                ComputeBackend.Cpu,
                requested != ComputeBackend.Cpu,
                "No model was supplied for backend selection.")
            : await SelectAsync(requested, model, cancellationToken).ConfigureAwait(false);
        return new ComputeDiagnosticsReport(
            layers,
            requested,
            selection.Selected,
            selection.Selected == ComputeBackend.Cuda
                ? "CUDA Ready (decode not yet verified)"
                : "CPU Active");
    }

    private static List<ComputeLayerStatus> ProbeCudaRuntimeLayers(ComputeDeviceInfo adapter)
    {
        var cudaRuntime = CanLoad("cudart64_12.dll", out var cudaMessage);
        var cudnn = CanLoad("cudnn64_9.dll", out var cudnnMessage);
        var provider = CanLoad("onnxruntime_providers_cuda.dll", out var providerMessage);
        var cudaWorker = File.Exists(Path.Combine(
            AppContext.BaseDirectory,
            "workers",
            "cuda-12",
            "RSTT.Speech.Worker.exe"));

        return
        [
            new(
                ComputeReadinessLayer.Hardware,
                true,
                $"{adapter.Name} detected (VEN_{adapter.VendorId:X4}, DEV_{adapter.DeviceId:X4}, {adapter.DedicatedMemory / 1024d / 1024d:N0} MiB dedicated)."),
            new(
                ComputeReadinessLayer.Driver,
                true,
                string.IsNullOrWhiteSpace(adapter.DriverVersion)
                    ? "NVIDIA adapter is available; driver version was not exposed by DXGI."
                    : $"NVIDIA driver {adapter.DriverVersion} is installed.",
                adapter.DriverVersion),
            new(
                ComputeReadinessLayer.CudaRuntime,
                cudaRuntime,
                cudaMessage,
                "12.x"),
            new(
                ComputeReadinessLayer.Cudnn,
                cudnn,
                cudnnMessage,
                "9.x"),
            new(
                ComputeReadinessLayer.SherpaCudaRuntime,
                cudaWorker,
                cudaWorker
                    ? "The versioned CUDA 12 worker is installed."
                    : "The optional versioned RSTT CUDA Accelerator Pack is not installed.",
                "sherpa-onnx 1.13.4"),
            new(
                ComputeReadinessLayer.ProviderLoad,
                provider,
                providerMessage),
            new(
                ComputeReadinessLayer.RecognizerLoad,
                false,
                "Not tested in this process."),
            new(
                ComputeReadinessLayer.Warmup,
                false,
                "Not tested in this process."),
            new(
                ComputeReadinessLayer.ActiveInference,
                false,
                "CUDA Active is reported only after a successful decode."),
        ];
    }

    private static bool CanLoad(string libraryName, out string message)
    {
        if (!NativeLibrary.TryLoad(libraryName, out var handle))
        {
            message = $"{libraryName} could not be loaded.";
            return false;
        }

        NativeLibrary.Free(handle);
        message = $"{libraryName} loaded successfully.";
        return true;
    }
}
