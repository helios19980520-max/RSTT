using Microsoft.Extensions.Logging;
using RSTT.Core.Abstractions;
using RSTT.Core.Models;

namespace RSTT.Speech;

public abstract partial class SherpaWorkerRecognitionEngine : ISpeechRecognitionEngine
{
    private readonly IModelManager _models;
    private readonly ISettingsService _settings;
    private readonly IComputeDeviceService _compute;
    private readonly IPerformanceMonitor _performance;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SherpaWorkerClient? _client;
    private SessionGenerationId _generation;
    private string _language = "auto";
    private bool _workerSessionStarted;
    private bool _started;
    private bool _cudaActive;
    private bool _disposed;

    protected SherpaWorkerRecognitionEngine(
        IModelManager models,
        ISettingsService settings,
        IComputeDeviceService compute,
        IPerformanceMonitor performance,
        ILogger logger)
    {
        _models = models;
        _settings = settings;
        _compute = compute;
        _performance = performance;
        _logger = logger;
        ModelInformation = models.GetSelectedModel();
    }

    public bool IsReady => _client is not null;

    public ModelInformation ModelInformation { get; private set; }

    public event EventHandler<RecognitionHypothesis>? RecognitionResultAvailable;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_client is null)
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
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await DisposeClientUnsafeAsync().ConfigureAwait(false);
            await LoadUnsafeAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UnloadAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await DisposeClientUnsafeAsync().ConfigureAwait(false);
            ModelInformation = _models.GetSelectedModel();
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        if (_client is null)
        {
            throw new InvalidOperationException("The sherpa worker is not initialized.");
        }

        _started = true;
        return Task.CompletedTask;
    }

    public async Task ProcessAudioAsync(
        AudioChunk chunk,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!_started || _client is null || chunk.Samples.Length == 0)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_workerSessionStarted)
            {
                _generation = chunk.SessionGenerationId;
                await _client
                    .StartSessionAsync(_generation, chunk.SequenceNumber, cancellationToken)
                    .ConfigureAwait(false);
                _workerSessionStarted = true;
            }
            else if (chunk.SessionGenerationId != _generation)
            {
                return;
            }

            var result = await _client
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
        if (_disposed)
        {
            return;
        }

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
        ThrowIfDisposed();
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
            _started = false;
            await DisposeClientUnsafeAsync().ConfigureAwait(false);
            GC.SuppressFinalize(this);
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
        var descriptor = installation.Descriptor ??
            throw new InvalidOperationException("The selected model descriptor is missing.");
        _language = ResolveLanguage(descriptor, _settings.Current.DefaultLanguage);
        var requested = _settings.Current.DefaultBackend;
        var candidates = ResolveCandidates(requested, descriptor);
        Exception? cudaFailure = null;
        _compute.ResetRuntimeEvidence();

        foreach (var backend in candidates)
        {
            SherpaWorkerClient? candidate = null;
            try
            {
                candidate = await SherpaWorkerClient
                    .StartAsync(backend, cancellationToken)
                    .ConfigureAwait(false);
                if (backend == ComputeBackend.Cuda)
                {
                    Report(
                        ComputeReadinessLayer.ProviderLoad,
                        ComputeLayerState.Ready,
                        $"CUDA worker handshake passed ({candidate.Handshake.RuntimeVersion}).",
                        descriptor.Id,
                        candidate.Handshake.RuntimeVersion);
                }

                installation = installation with
                {
                    Provider = backend == ComputeBackend.Cuda ? "cuda" : "cpu",
                    NumThreads = ResolveThreadCount(installation, backend),
                };
                _ = await candidate
                    .LoadAsync(installation, _language, cancellationToken)
                    .ConfigureAwait(false);
                if (backend == ComputeBackend.Cuda)
                {
                    Report(
                        ComputeReadinessLayer.RecognizerLoad,
                        ComputeLayerState.Ready,
                        $"{descriptor.DisplayName} loaded in the CUDA worker.",
                        descriptor.Id,
                        candidate.Handshake.RuntimeVersion);
                }

                var warmup = await candidate
                    .WarmupAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (backend == ComputeBackend.Cuda)
                {
                    Report(
                        ComputeReadinessLayer.Warmup,
                        ComputeLayerState.Ready,
                        $"CUDA warmup decode passed in {warmup.Performance?.DecodeMilliseconds:N1} ms.",
                        descriptor.Id,
                        candidate.Handshake.RuntimeVersion);
                    Report(
                        ComputeReadinessLayer.ActiveInference,
                        ComputeLayerState.NotTested,
                        "Warmup passed; awaiting active-session audio decode.",
                        descriptor.Id,
                        candidate.Handshake.RuntimeVersion);
                }

                _client = candidate;
                candidate = null;
                ModelInformation = installation.Information;
                _workerSessionStarted = false;
                _cudaActive = false;
                _performance.SetSessionContext(
                    backend == ComputeBackend.Cuda ? "cuda-ready" : "cpu",
                    descriptor.Id,
                    string.Empty);
                LogWorkerLoaded(
                    _logger,
                    descriptor.Id,
                    backend.ToString(),
                    _client.Handshake.RuntimeVersion);
                return;
            }
            catch (Exception exception) when (
                backend == ComputeBackend.Cuda &&
                requested == ComputeBackend.Auto)
            {
                cudaFailure = exception;
                Report(
                    ComputeReadinessLayer.ProviderLoad,
                    ComputeLayerState.Failed,
                    $"CUDA worker failed; CPU fallback selected: {exception.Message}",
                    descriptor.Id);
                if (candidate is not null)
                {
                    await candidate.DisposeAsync().ConfigureAwait(false);
                }
            }
            catch
            {
                if (candidate is not null)
                {
                    await candidate.DisposeAsync().ConfigureAwait(false);
                }

                throw;
            }
        }

        throw new InvalidOperationException(
            "No speech worker could be started.",
            cudaFailure);
    }

    private async Task FinishSessionUnsafeAsync(CancellationToken cancellationToken)
    {
        if (!_workerSessionStarted || _client is null)
        {
            return;
        }

        var result = await _client.FinishAsync(cancellationToken).ConfigureAwait(false);
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
            if (!_cudaActive && _client?.Backend == ComputeBackend.Cuda)
            {
                _cudaActive = true;
                _performance.SetSessionContext(
                    "cuda",
                    ModelInformation.Id,
                    string.Empty);
                Report(
                    ComputeReadinessLayer.ActiveInference,
                    ComputeLayerState.Active,
                    $"CUDA Active: session audio decoded in {performance.DecodeMilliseconds:N1} ms.",
                    ModelInformation.Id,
                    _client.Handshake.RuntimeVersion);
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
                    hypothesis.IsFinal,
                    DateTimeOffset.UtcNow,
                    hypothesis.Language,
                    ModelInformation.Id));
        }
    }

    private async Task DisposeClientUnsafeAsync()
    {
        _started = false;
        _workerSessionStarted = false;
        _cudaActive = false;
        if (_client is not null)
        {
            await _client.DisposeAsync().ConfigureAwait(false);
            _client = null;
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

    private int ResolveThreadCount(
        ModelInstallation installation,
        ComputeBackend backend)
    {
        if (backend == ComputeBackend.Cuda)
        {
            return Math.Clamp(installation.NumThreads, 1, 4);
        }

        if (_settings.Current.CpuThreadLimit > 0)
        {
            return Math.Clamp(
                _settings.Current.CpuThreadLimit,
                1,
                Math.Min(8, Environment.ProcessorCount));
        }

        return Math.Clamp(
            installation.NumThreads,
            1,
            Math.Min(4, Environment.ProcessorCount));
    }

    private static IReadOnlyList<ComputeBackend> ResolveCandidates(
        ComputeBackend requested,
        ModelDescriptor descriptor) =>
        requested switch
        {
            ComputeBackend.Cpu => [ComputeBackend.Cpu],
            ComputeBackend.Cuda when !descriptor.CudaSupported =>
                throw new InvalidOperationException(
                    $"{descriptor.DisplayName} does not support CUDA."),
            ComputeBackend.Cuda => [ComputeBackend.Cuda],
            _ when descriptor.CudaSupported =>
                [ComputeBackend.Cuda, ComputeBackend.Cpu],
            _ => [ComputeBackend.Cpu],
        };

    private static string ResolveLanguage(
        ModelDescriptor descriptor,
        string configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return "auto";
        }

        if (configured.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return descriptor.Capabilities.SupportsLanguageDetection
                ? "auto"
                : descriptor.Languages.Count > 0
                    ? descriptor.Languages[0]
                    : "en";
        }

        return configured.Trim();
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed, this);

    [LoggerMessage(
        LogLevel.Information,
        "Loaded {ModelId} in isolated {Backend} sherpa worker {RuntimeVersion}.")]
    private static partial void LogWorkerLoaded(
        ILogger logger,
        string modelId,
        string backend,
        string runtimeVersion);
}
