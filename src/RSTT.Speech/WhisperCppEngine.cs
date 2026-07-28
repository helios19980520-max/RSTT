using Microsoft.Extensions.Logging;
using RSTT.Core.Abstractions;
using RSTT.Core.Models;
using RSTT.Core.Workers;

namespace RSTT.Speech;

/// <summary>
/// Runs Whisper and Silero VAD together inside one isolated CPU or CUDA worker.
/// Only final VAD-segment hypotheses leave the worker.
/// </summary>
public sealed partial class WhisperCppEngine : ISpeechRecognitionEngine
{
    private readonly IModelManager _models;
    private readonly ISettingsService _settings;
    private readonly IComputeDeviceService _compute;
    private readonly IPerformanceMonitor _performance;
    private readonly ILogger<WhisperCppEngine> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private WhisperWorkerClient? _worker;
    private SessionGenerationId _generation;
    private bool _workerSessionStarted;
    private bool _started;
    private bool _cudaActive;
    private bool _disposed;

    public WhisperCppEngine(
        IModelManager models,
        ISettingsService settings,
        IComputeDeviceService compute,
        IPerformanceMonitor performance,
        ILogger<WhisperCppEngine> logger)
    {
        _models = models;
        _settings = settings;
        _compute = compute;
        _performance = performance;
        _logger = logger;
        ModelInformation = models.GetSelectedModel();
    }

    public bool IsReady => _worker is not null;

    public ModelInformation ModelInformation { get; private set; }

    public event EventHandler<RecognitionHypothesis>? RecognitionResultAvailable;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_worker is null)
            {
                await LoadUnsafeAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await DisposeWorkerUnsafeAsync().ConfigureAwait(false);
            await LoadUnsafeAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UnloadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await DisposeWorkerUnsafeAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_worker is null)
        {
            throw new InvalidOperationException("The Whisper worker is not initialized.");
        }

        _started = true;
        return Task.CompletedTask;
    }

    public async Task ProcessAudioAsync(
        AudioChunk chunk,
        CancellationToken cancellationToken = default)
    {
        if (!_started || _worker is null || chunk.Samples.Length == 0)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_workerSessionStarted)
            {
                _generation = chunk.SessionGenerationId;
                await _worker
                    .StartSessionAsync(_generation.Value, chunk.SequenceNumber, cancellationToken)
                    .ConfigureAwait(false);
                _workerSessionStarted = true;
            }
            else if (chunk.SessionGenerationId != _generation)
            {
                return;
            }

            var result = await _worker
                .ProcessAudioAsync(chunk, cancellationToken)
                .ConfigureAwait(false);
            PublishBatch(result);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _started = false;
            await FinishSessionUnsafeAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await FinishSessionUnsafeAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _disposed = true;
            await DisposeWorkerUnsafeAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private async Task LoadUnsafeAsync(CancellationToken cancellationToken)
    {
        var installation = _models.GetSelectedInstallation();
        var descriptor = installation.Descriptor
            ?? throw new InvalidOperationException("The Whisper model descriptor is missing.");
        if (!descriptor.Engine.Equals("whisper-cpp", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{descriptor.DisplayName} is not a whisper.cpp model.");
        }

        var language = string.IsNullOrWhiteSpace(_settings.Current.DefaultLanguage)
            ? "auto"
            : _settings.Current.DefaultLanguage;
        var requested = _settings.Current.DefaultBackend;
        var candidates = requested switch
        {
            ComputeBackend.Cpu => new[] { ComputeBackend.Cpu },
            ComputeBackend.Cuda => new[] { ComputeBackend.Cuda },
            _ => new[] { ComputeBackend.Cuda, ComputeBackend.Cpu },
        };
        Exception? lastFailure = null;
        _compute.ResetRuntimeEvidence();
        foreach (var backend in candidates)
        {
            WhisperWorkerClient? candidate = null;
            try
            {
                candidate = await WhisperWorkerClient
                    .StartAsync(backend, cancellationToken)
                    .ConfigureAwait(false);
                if (backend == ComputeBackend.Cuda)
                {
                    Report(
                        ComputeReadinessLayer.ProviderLoad,
                        ComputeLayerState.Ready,
                        $"Whisper CUDA worker handshake passed ({candidate.Handshake.RuntimeVersion}).",
                        descriptor.Id,
                        candidate.Handshake.RuntimeVersion);
                }

                var policy = descriptor.VadPolicy ?? new VadSegmentationPolicy();
                await candidate.LoadAsync(
                        RequiredFile(installation.Files, "model"),
                        RequiredFile(installation.Files, "vad"),
                        language,
                        installation.NumThreads,
                        new WorkerVadPolicy(
                            policy.PreRollMs,
                            policy.PostRollMs,
                            policy.MaximumSegmentMs,
                            policy.Threshold),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (backend == ComputeBackend.Cuda)
                {
                    Report(
                        ComputeReadinessLayer.RecognizerLoad,
                        ComputeLayerState.Ready,
                        $"{descriptor.DisplayName} loaded in the Whisper CUDA worker.",
                        descriptor.Id,
                        candidate.Handshake.RuntimeVersion);
                }

                await candidate.WarmupAsync(cancellationToken).ConfigureAwait(false);
                if (backend == ComputeBackend.Cuda)
                {
                    Report(
                        ComputeReadinessLayer.Warmup,
                        ComputeLayerState.Ready,
                        "Whisper CUDA warmup decode passed.",
                        descriptor.Id,
                        candidate.Handshake.RuntimeVersion);
                    Report(
                        ComputeReadinessLayer.ActiveInference,
                        ComputeLayerState.NotTested,
                        "Warmup passed; awaiting active-session speech decode.",
                        descriptor.Id,
                        candidate.Handshake.RuntimeVersion);
                }

                _worker = candidate;
                candidate = null;
                break;
            }
            catch (Exception exception) when (
                requested == ComputeBackend.Auto &&
                backend == ComputeBackend.Cuda)
            {
                lastFailure = exception;
                if (candidate is not null)
                {
                    await candidate.DisposeAsync().ConfigureAwait(false);
                }

                Report(
                    ComputeReadinessLayer.ProviderLoad,
                    ComputeLayerState.Failed,
                    $"Whisper CUDA verification failed; CPU fallback selected: {exception.Message}",
                    descriptor.Id);
                LogCudaFallback(_logger, exception.Message);
            }
        }

        if (_worker is null)
        {
            throw new InvalidOperationException(
                $"No Whisper worker could be started. {lastFailure?.Message}",
                lastFailure);
        }

        ModelInformation = installation.Information;
        _workerSessionStarted = false;
        _cudaActive = false;
        _performance.SetSessionContext(
            _worker.Backend == ComputeBackend.Cuda ? "cuda-ready" : "cpu",
            descriptor.Id,
            _worker.Handshake.RuntimeVersion);
        LogWhisperLoaded(
            _logger,
            descriptor.Id,
            _worker.Backend.ToString(),
            _worker.Handshake.RuntimeVersion);
    }

    private async Task FinishSessionUnsafeAsync(CancellationToken cancellationToken)
    {
        if (!_workerSessionStarted || _worker is null)
        {
            return;
        }

        var result = await _worker.FinishAsync(cancellationToken).ConfigureAwait(false);
        _workerSessionStarted = false;
        PublishBatch(result);
    }

    private void PublishBatch(WorkerBatchResult result)
    {
        if (result.Performance is { } performance &&
            performance.DecodeMilliseconds > 0)
        {
            _performance.RecordDecode(
                TimeSpan.FromMilliseconds(performance.DecodeMilliseconds),
                performance.AudioMilliseconds);
            if (!_cudaActive && _worker?.Backend == ComputeBackend.Cuda)
            {
                _cudaActive = true;
                _performance.SetSessionContext(
                    "cuda",
                    ModelInformation.Id,
                    _worker.Handshake.RuntimeVersion);
                Report(
                    ComputeReadinessLayer.ActiveInference,
                    ComputeLayerState.Active,
                    $"CUDA Active: Whisper decoded a speech segment in {performance.DecodeMilliseconds:N1} ms.",
                    ModelInformation.Id,
                    _worker.Handshake.RuntimeVersion);
            }
        }

        foreach (var hypothesis in result.Hypotheses)
        {
            if (string.IsNullOrWhiteSpace(hypothesis.Text))
            {
                continue;
            }

            _performance.RecordRecognitionResult();
            RecognitionResultAvailable?.Invoke(
                this,
                new RecognitionHypothesis(
                    new SessionGenerationId(hypothesis.SessionGenerationId),
                    hypothesis.SequenceId,
                    hypothesis.Text,
                    true,
                    DateTimeOffset.UtcNow,
                    hypothesis.Language,
                    ModelInformation.Id,
                    hypothesis.Confidence));
        }
    }

    private async Task DisposeWorkerUnsafeAsync()
    {
        _started = false;
        _workerSessionStarted = false;
        _cudaActive = false;
        if (_worker is not null)
        {
            await _worker.DisposeAsync().ConfigureAwait(false);
            _worker = null;
        }
    }

    private void Report(
        ComputeReadinessLayer layer,
        ComputeLayerState state,
        string status,
        string modelId,
        string runtimeVersion = "") =>
        _compute.ReportRuntimeEvidence(
            new ComputeRuntimeEvidence(
                ComputeBackend.Cuda,
                layer,
                state,
                status,
                modelId,
                runtimeVersion));

    private static string RequiredFile(
        IReadOnlyDictionary<string, string> files,
        string key) =>
        files.TryGetValue(key, out var path) && !string.IsNullOrWhiteSpace(path)
            ? path
            : throw new InvalidOperationException(
                $"The selected Whisper model is missing its required '{key}' artifact.");

    [LoggerMessage(
        LogLevel.Information,
        "Loaded Whisper model {ModelId} in isolated {Backend} worker using {RuntimeVersion}.")]
    private static partial void LogWhisperLoaded(
        ILogger logger,
        string modelId,
        string backend,
        string runtimeVersion);

    [LoggerMessage(
        LogLevel.Warning,
        "Whisper CUDA worker failed verification and was terminated before CPU fallback: {Reason}")]
    private static partial void LogCudaFallback(ILogger logger, string reason);
}
