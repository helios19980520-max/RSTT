using System.ComponentModel;
using System.Diagnostics;
using RSTT.Core.Abstractions;
using RSTT.Core.Compute;
using RSTT.Core.Models;

namespace RSTT.Infrastructure;

/// <summary>
/// Reports hardware, app-local runtime files, and verified execution as
/// separate facts. File presence never makes CUDA selectable by itself.
/// </summary>
public sealed class WindowsComputeDeviceService : IComputeDeviceService
{
    private const string NvidiaDriverUrl = "https://www.nvidia.com/Download/index.aspx";
    private const string CudaArchiveUrl = "https://developer.nvidia.com/cuda-toolkit-archive";
    private const string CudnnUrl = "https://developer.nvidia.com/cudnn-downloads";
    private const string SherpaCudaUrl =
        "https://k2-fsa.github.io/sherpa/onnx/install/windows/build-cuda.html";
    private const string OnnxCudaUrl =
        "https://onnxruntime.ai/docs/execution-providers/CUDA-ExecutionProvider.html";
    private const string AcceleratorPackUrl =
        "https://github.com/helios19980520-max/RSTT/releases";
    private const string VcRuntimeUrl =
        "https://aka.ms/vs/17/release/vc_redist.x64.exe";
    private readonly IHardwareDetectionService _hardware;
    private readonly object _evidenceGate = new();
    private readonly Dictionary<ComputeReadinessLayer, ComputeRuntimeEvidence> _runtimeEvidence = [];
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
            new(ComputeBackend.Cpu, true, "The CPU sherpa runtime is available.", cpu),
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
        var anyWorkerReady = layers.Any(layer =>
            (layer.Layer is ComputeReadinessLayer.SherpaCudaRuntime or
                ComputeReadinessLayer.WhisperCudaRuntime) &&
            layer.IsReady);
        var firstBlocking = layers.FirstOrDefault(layer =>
            (layer.Layer is ComputeReadinessLayer.CudaRuntime or
                ComputeReadinessLayer.Cudnn or
                ComputeReadinessLayer.ProviderLoad) &&
            layer.State is ComputeLayerState.Missing or
                ComputeLayerState.Incompatible or
                ComputeLayerState.Failed);
        var available = anyWorkerReady && firstBlocking is null;
        probes.Add(new ComputeBackendProbe(
            ComputeBackend.Cuda,
            available,
            available
                ? "A CUDA worker and its native runtime files are installed; model load and warmup will verify execution."
                : firstBlocking?.Status ??
                    "No model-appropriate CUDA worker is installed.",
            nvidia));
        return _cached = probes;
    }

    public async Task<ComputeSelectionResult> SelectAsync(
        ComputeBackend requested,
        ModelDescriptor model,
        CancellationToken cancellationToken = default)
    {
        var probes = await ProbeAsync(cancellationToken).ConfigureAwait(false);
        var selected = ComputeSelectionPolicy.Select(requested, model, probes);
        if (selected.Selected != ComputeBackend.Cuda)
        {
            return selected;
        }

        var workerPath = ResolveWorkerPath(model);
        if (File.Exists(workerPath))
        {
            return selected with
            {
                Reason = $"CUDA preflight passed for {Path.GetFileName(workerPath)}. Worker load and warmup are required.",
            };
        }

        var cpu = probes.FirstOrDefault(probe => probe.Backend == ComputeBackend.Cpu);
        return new ComputeSelectionResult(
            requested,
            ComputeBackend.Cpu,
            true,
            $"The CUDA worker required by {model.DisplayName} is missing: {workerPath}",
            cpu?.Device);
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
                    "No physical NVIDIA adapter was detected through DXGI.",
                    State: ComputeLayerState.Missing,
                    RemediationUrl: NvidiaDriverUrl),
            }
            : ProbeCudaRuntimeLayers(nvidia);
        if (model is not null)
        {
            layers.Add(new ComputeLayerStatus(
                ComputeReadinessLayer.ModelCompatibility,
                model.CudaSupported,
                model.CudaSupported
                    ? $"{model.DisplayName} declares CUDA compatibility."
                    : $"{model.DisplayName} is CPU-only.",
                model.Revision,
                model.CudaSupported
                    ? ComputeLayerState.Ready
                    : ComputeLayerState.Incompatible));
        }

        lock (_evidenceGate)
        {
            foreach (var evidence in _runtimeEvidence.Values)
            {
                var index = layers.FindIndex(layer => layer.Layer == evidence.Layer);
                var status = new ComputeLayerStatus(
                    evidence.Layer,
                    evidence.IsReady,
                    evidence.Status,
                    evidence.RuntimeVersion,
                    evidence.State,
                    DetectedPath: evidence.DetectedPath);
                if (index >= 0)
                {
                    layers[index] = status;
                }
                else
                {
                    layers.Add(status);
                }
            }
        }

        var selection = model is null
            ? new ComputeSelectionResult(
                requested,
                ComputeBackend.Cpu,
                requested != ComputeBackend.Cpu,
                "No model was supplied for backend selection.")
            : await SelectAsync(requested, model, cancellationToken).ConfigureAwait(false);
        var active = layers.Any(layer =>
            layer.Layer == ComputeReadinessLayer.ActiveInference &&
            layer.State == ComputeLayerState.Active);
        return new ComputeDiagnosticsReport(
            layers,
            requested,
            active ? ComputeBackend.Cuda : selection.Selected,
            active
                ? "CUDA Active"
                : selection.Selected == ComputeBackend.Cuda
                    ? "CUDA Ready (decode not yet verified)"
                : "CPU Active");
    }

    public void ReportRuntimeEvidence(ComputeRuntimeEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        lock (_evidenceGate)
        {
            _runtimeEvidence[evidence.Layer] = evidence with
            {
                ObservedAt = evidence.ObservedAt ?? DateTimeOffset.UtcNow,
            };
        }
    }

    public void ResetRuntimeEvidence()
    {
        lock (_evidenceGate)
        {
            _runtimeEvidence.Clear();
        }
    }

    private static List<ComputeLayerStatus> ProbeCudaRuntimeLayers(
        ComputeDeviceInfo adapter)
    {
        var supplemental = TryGetNvidiaInfo();
        var workerRoot = Path.Combine(
            AppContext.BaseDirectory,
            "workers",
            "sherpa-cuda12",
            "1.13.4");
        var cudaWorkerPath = Path.Combine(workerRoot, "RSTT.Speech.Worker.exe");
        var whisperWorkerRoot = Path.Combine(
            AppContext.BaseDirectory,
            "workers",
            "whisper-cuda12",
            "1.9.1");
        var whisperWorkerPath = Path.Combine(
            whisperWorkerRoot,
            "RSTT.Whisper.Cuda12.Worker.exe");
        var vcRuntimePath = Path.Combine(Environment.SystemDirectory, "vcruntime140.dll");
        var cuda = FindLibrary("cudart64_12.dll", workerRoot);
        var anyCuda = cuda.Path.Length == 0
            ? FindAnyCudaRuntime()
            : RuntimeFile.Empty;
        var cudnn = FindLibrary("cudnn64_9.dll", workerRoot);
        var provider = FindLibrary("onnxruntime_providers_cuda.dll", workerRoot);
        var workerExists = File.Exists(cudaWorkerPath);

        var cudaState = cuda.Path.Length > 0
            ? ComputeLayerState.Ready
            : anyCuda.Path.Length > 0
                ? ComputeLayerState.Incompatible
                : ComputeLayerState.Missing;
        var cudaStatus = cudaState switch
        {
            ComputeLayerState.Ready =>
                "The required CUDA 12 runtime is present.",
            ComputeLayerState.Incompatible =>
                $"CUDA {anyCuda.Version} is installed, but this sherpa worker requires CUDA 12.x.",
            _ => "cudart64_12.dll is missing.",
        };

        return
        [
            new(
                ComputeReadinessLayer.SystemRuntime,
                File.Exists(vcRuntimePath),
                File.Exists(vcRuntimePath)
                    ? "Microsoft Visual C++ 2015-2022 x64 runtime is present."
                    : "Microsoft Visual C++ 2015-2022 x64 runtime is missing.",
                File.Exists(vcRuntimePath)
                    ? FileVersionInfo.GetVersionInfo(vcRuntimePath).FileVersion ?? string.Empty
                    : string.Empty,
                File.Exists(vcRuntimePath)
                    ? ComputeLayerState.Ready
                    : ComputeLayerState.Missing,
                "Visual C++ 2015-2022 x64",
                File.Exists(vcRuntimePath) ? vcRuntimePath : string.Empty,
                VcRuntimeUrl),
            new(
                ComputeReadinessLayer.Hardware,
                true,
                $"{adapter.Name} detected (VEN_{adapter.VendorId:X4}, DEV_{adapter.DeviceId:X4}, {adapter.DedicatedMemory / 1024d / 1024d:N0} MiB dedicated).",
                supplemental.ComputeCapability,
                ComputeLayerState.Ready),
            new(
                ComputeReadinessLayer.Driver,
                true,
                supplemental.DriverVersion.Length == 0
                    ? "The NVIDIA adapter is active; its driver version was not available."
                    : $"NVIDIA driver {supplemental.DriverVersion} is installed.",
                supplemental.DriverVersion,
                ComputeLayerState.Ready,
                RemediationUrl: NvidiaDriverUrl),
            new(
                ComputeReadinessLayer.CudaRuntime,
                cuda.Path.Length > 0,
                cudaStatus,
                cuda.Path.Length > 0 ? "12.x" : anyCuda.Version,
                cudaState,
                "12.x",
                cuda.Path.Length > 0 ? cuda.Path : anyCuda.Path,
                CudaArchiveUrl),
            new(
                ComputeReadinessLayer.Cudnn,
                cudnn.Path.Length > 0,
                cudnn.Path.Length > 0
                    ? "The required cuDNN 9 runtime is present."
                    : "cudnn64_9.dll is missing.",
                cudnn.Path.Length > 0 ? "9.x" : string.Empty,
                cudnn.Path.Length > 0
                    ? ComputeLayerState.Ready
                    : ComputeLayerState.Missing,
                "9.x",
                cudnn.Path,
                CudnnUrl),
            new(
                ComputeReadinessLayer.SherpaCudaRuntime,
                workerExists,
                workerExists
                    ? "The versioned sherpa CUDA worker is installed."
                    : "The optional RSTT CUDA Accelerator Pack is not installed.",
                workerExists ? "sherpa-onnx 1.13.4" : string.Empty,
                workerExists
                    ? ComputeLayerState.Ready
                    : ComputeLayerState.Missing,
                "sherpa-onnx 1.13.4 / CUDA 12.x",
                workerExists ? cudaWorkerPath : string.Empty,
                workerExists ? SherpaCudaUrl : AcceleratorPackUrl),
            new(
                ComputeReadinessLayer.WhisperCudaRuntime,
                File.Exists(whisperWorkerPath),
                File.Exists(whisperWorkerPath)
                    ? "The versioned whisper.cpp CUDA 12 worker is installed."
                    : "The optional Whisper CUDA worker is not installed.",
                File.Exists(whisperWorkerPath) ? "Whisper.net 1.9.1 / whisper.cpp" : string.Empty,
                File.Exists(whisperWorkerPath)
                    ? ComputeLayerState.Ready
                    : ComputeLayerState.Missing,
                "Whisper.net 1.9.1 / CUDA 12.x",
                File.Exists(whisperWorkerPath) ? whisperWorkerPath : string.Empty,
                AcceleratorPackUrl),
            new(
                ComputeReadinessLayer.ProviderLoad,
                false,
                provider.Path.Length > 0
                    ? "The CUDA provider file is present; an isolated worker self-test has not loaded it yet."
                    : "onnxruntime_providers_cuda.dll is missing.",
                provider.Path.Length > 0 ? "File present" : string.Empty,
                provider.Path.Length > 0
                    ? ComputeLayerState.NotTested
                    : ComputeLayerState.Missing,
                "Matching ONNX Runtime CUDA provider",
                provider.Path,
                OnnxCudaUrl),
            new(
                ComputeReadinessLayer.RecognizerLoad,
                false,
                "Not tested by an isolated worker.",
                State: ComputeLayerState.NotTested),
            new(
                ComputeReadinessLayer.Warmup,
                false,
                "Not tested by an isolated worker.",
                State: ComputeLayerState.NotTested),
            new(
                ComputeReadinessLayer.ActiveInference,
                false,
                "CUDA Active is reported only after a successful model decode.",
                State: ComputeLayerState.NotTested),
        ];
    }

    private static string ResolveWorkerPath(ModelDescriptor model)
    {
        var isWhisper = model.Engine.Equals(
            "whisper-cpp",
            StringComparison.OrdinalIgnoreCase);
        return isWhisper
            ? Path.Combine(
                AppContext.BaseDirectory,
                "workers",
                "whisper-cuda12",
                "1.9.1",
                "RSTT.Whisper.Cuda12.Worker.exe")
            : Path.Combine(
                AppContext.BaseDirectory,
                "workers",
                "sherpa-cuda12",
                "1.13.4",
                "RSTT.Speech.Worker.exe");
    }

    private static RuntimeFile FindLibrary(string fileName, string workerRoot)
    {
        var localPath = Path.Combine(workerRoot, fileName);
        if (File.Exists(localPath))
        {
            return new RuntimeFile(localPath, ExtractRuntimeVersion(fileName));
        }

        foreach (var item in (
                     Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                 .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = item.Trim().Trim('"');
            if (directory.Length == 0)
            {
                continue;
            }

            var path = Path.Combine(directory, fileName);
            if (File.Exists(path))
            {
                return new RuntimeFile(path, ExtractRuntimeVersion(fileName));
            }
        }

        return RuntimeFile.Empty;
    }

    private static RuntimeFile FindAnyCudaRuntime()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var item in path.Split(
                     Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = item.Trim().Trim('"');
            if (!Directory.Exists(directory))
            {
                continue;
            }

            try
            {
                var candidate = Directory
                    .EnumerateFiles(directory, "cudart64_*.dll")
                    .OrderByDescending(static value => value, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                if (candidate is not null)
                {
                    return new RuntimeFile(
                        candidate,
                        ExtractRuntimeVersion(Path.GetFileName(candidate)));
                }
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (IOException)
            {
            }
        }

        return RuntimeFile.Empty;
    }

    private static string ExtractRuntimeVersion(string fileName)
    {
        const string cudaPrefix = "cudart64_";
        if (fileName.StartsWith(cudaPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var version = Path.GetFileNameWithoutExtension(fileName)[cudaPrefix.Length..];
            return version.Length == 0 ? string.Empty : $"{version}.x";
        }

        const string cudnnPrefix = "cudnn64_";
        if (fileName.StartsWith(cudnnPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var version = Path.GetFileNameWithoutExtension(fileName)[cudnnPrefix.Length..];
            return version.Length == 0 ? string.Empty : $"{version}.x";
        }

        return string.Empty;
    }

    private static NvidiaInfo TryGetNvidiaInfo()
    {
        try
        {
            var startInfo = new ProcessStartInfo("nvidia-smi")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            startInfo.ArgumentList.Add(
                "--query-gpu=driver_version,compute_cap");
            startInfo.ArgumentList.Add("--format=csv,noheader,nounits");
            using var process = Process.Start(startInfo);
            if (process is null || !process.WaitForExit(2_000))
            {
                return NvidiaInfo.Empty;
            }

            var fields = process.StandardOutput.ReadToEnd()
                .Split(',', StringSplitOptions.TrimEntries);
            return fields.Length >= 2
                ? new NvidiaInfo(fields[0], fields[1])
                : NvidiaInfo.Empty;
        }
        catch (Exception exception) when (
            exception is Win32Exception or
                InvalidOperationException or
                IOException)
        {
            return NvidiaInfo.Empty;
        }
    }

    private sealed record RuntimeFile(string Path, string Version)
    {
        public static RuntimeFile Empty { get; } = new(string.Empty, string.Empty);
    }

    private sealed record NvidiaInfo(string DriverVersion, string ComputeCapability)
    {
        public static NvidiaInfo Empty { get; } = new(string.Empty, string.Empty);
    }
}
