using Microsoft.Extensions.Logging;
using RSTT.Core.Abstractions;
using RSTT.Core.Models;
using SherpaOnnx;

namespace RSTT.Speech;

/// <summary>
/// Segments 16 kHz audio with Silero VAD and sends complete segments to one
/// isolated whisper.cpp worker. Only final segment hypotheses leave this engine.
/// </summary>
public sealed partial class WhisperCppEngine : ISpeechRecognitionEngine
{
    private readonly IModelManager _models;
    private readonly ISettingsService _settings;
    private readonly IPerformanceMonitor _performance;
    private readonly ILogger<WhisperCppEngine> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private WhisperWorkerClient? _worker;
    private VoiceActivityDetector? _vad;
    private SessionGenerationId _generation;
    private long _sequence;
    private string _language = "auto";
    private bool _started;
    private bool _disposed;

    public WhisperCppEngine(
        IModelManager models,
        ISettingsService settings,
        IPerformanceMonitor performance,
        ILogger<WhisperCppEngine> logger)
    {
        _models = models;
        _settings = settings;
        _performance = performance;
        _logger = logger;
        ModelInformation = models.GetSelectedModel();
    }

    public bool IsReady => _worker is not null && _vad is not null;

    public ModelInformation ModelInformation { get; private set; }

    public event EventHandler<RecognitionHypothesis>? RecognitionResultAvailable;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsReady)
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
            await DisposeResourcesUnsafeAsync().ConfigureAwait(false);
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
            await DisposeResourcesUnsafeAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsReady)
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
        if (!_started || chunk.Samples.Length == 0)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _generation = chunk.SessionGenerationId;
            _sequence = chunk.SequenceNumber;
            _vad?.AcceptWaveform(chunk.Samples);
            await DrainSegmentsUnsafeAsync(cancellationToken).ConfigureAwait(false);
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
            _vad?.Flush();
            await DrainSegmentsUnsafeAsync(cancellationToken).ConfigureAwait(false);
            _vad?.Reset();
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
            _vad?.Reset();
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
            await DisposeResourcesUnsafeAsync().ConfigureAwait(false);
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
        if (!string.Equals(descriptor.Engine, "whisper-cpp", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"{descriptor.DisplayName} is not a whisper.cpp model.");
        }

        _language = string.IsNullOrWhiteSpace(_settings.Current.DefaultLanguage)
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
        foreach (var backend in candidates)
        {
            WhisperWorkerClient? candidate = null;
            try
            {
                candidate = await WhisperWorkerClient.StartAsync(backend, cancellationToken)
                    .ConfigureAwait(false);
                await candidate.LoadAsync(
                        RequiredFile(installation.Files, "model"),
                        _language,
                        installation.NumThreads,
                        cancellationToken)
                    .ConfigureAwait(false);
                await candidate.WarmupAsync(cancellationToken).ConfigureAwait(false);
                _worker = candidate;
                break;
            }
            catch (Exception exception) when (
                requested == ComputeBackend.Auto &&
                backend == ComputeBackend.Cuda &&
                exception is IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                lastFailure = exception;
                if (candidate is not null)
                {
                    await candidate.DisposeAsync().ConfigureAwait(false);
                }

                LogCudaFallback(_logger, exception.Message);
            }
        }

        if (_worker is null)
        {
            throw new InvalidOperationException(
                $"No Whisper worker could be started. {lastFailure?.Message}",
                lastFailure);
        }

        try
        {
            var policy = descriptor.VadPolicy ?? new VadSegmentationPolicy();
            _vad = new VoiceActivityDetector(
                new VadModelConfig
                {
                    SampleRate = AudioChunk.SampleRate,
                    NumThreads = installation.NumThreads,
                    Provider = "cpu",
                    Debug = 0,
                    SileroVad = new SileroVadModelConfig
                    {
                        Model = RequiredFile(installation.Files, "vad"),
                        Threshold = policy.Threshold,
                        MinSilenceDuration = policy.PostRollMs / 1000f,
                        MinSpeechDuration = 0.15f,
                        WindowSize = 512,
                        MaxSpeechDuration = policy.MaximumSegmentMs / 1000f,
                    },
                },
                30f);
        }
        catch
        {
            await _worker.DisposeAsync().ConfigureAwait(false);
            _worker = null;
            throw;
        }

        ModelInformation = installation.Information;
        _performance.SetSessionContext(
            _worker.Backend == ComputeBackend.Cuda ? "cuda-active" : "cpu",
            descriptor.Id,
            _worker.Handshake.RuntimeVersion);
        LogWhisperLoaded(
            _logger,
            descriptor.Id,
            _worker.Backend.ToString(),
            _worker.Handshake.RuntimeVersion);
    }

    private async Task DrainSegmentsUnsafeAsync(CancellationToken cancellationToken)
    {
        if (_worker is null || _vad is null)
        {
            return;
        }

        while (!_vad.IsEmpty())
        {
            var segment = _vad.Front();
            _vad.Pop();
            if (segment.Samples.Length == 0)
            {
                continue;
            }

            var (hypothesis, performance) = await _worker
                .DecodeAsync(_generation.Value, _sequence, segment.Samples, cancellationToken)
                .ConfigureAwait(false);
            _performance.RecordDecode(
                TimeSpan.FromMilliseconds(performance.DecodeMilliseconds),
                performance.AudioMilliseconds);
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

    private async Task DisposeResourcesUnsafeAsync()
    {
        _started = false;
        _vad?.Dispose();
        _vad = null;
        if (_worker is not null)
        {
            await _worker.DisposeAsync().ConfigureAwait(false);
            _worker = null;
        }
    }

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
