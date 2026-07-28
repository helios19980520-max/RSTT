using System.Diagnostics;
using Microsoft.Extensions.Logging;
using RSTT.Core.Abstractions;
using RSTT.Core.Models;
using SherpaOnnx;

namespace RSTT.Speech;

/// <summary>
/// Segments audio with external Silero VAD and emits only complete offline
/// recognition segments.
/// </summary>
public sealed partial class SherpaOfflineVadEngine : ISpeechRecognitionEngine
{
    private readonly IModelManager _models;
    private readonly ISettingsService _settings;
    private readonly IComputeDeviceService _compute;
    private readonly IPerformanceMonitor _performance;
    private readonly ILogger<SherpaOfflineVadEngine> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private OfflineRecognizer? _recognizer;
    private VoiceActivityDetector? _vad;
    private SessionGenerationId _generation;
    private long _sequence;
    private string _language = "auto";
    private bool _started;
    private bool _disposed;

    public SherpaOfflineVadEngine(
        IModelManager models,
        ISettingsService settings,
        IComputeDeviceService compute,
        IPerformanceMonitor performance,
        ILogger<SherpaOfflineVadEngine> logger)
    {
        _models = models;
        _settings = settings;
        _compute = compute;
        _performance = performance;
        _logger = logger;
        ModelInformation = models.GetSelectedModel();
    }

    public bool IsReady => _recognizer is not null && _vad is not null;

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
            DisposeNativeUnsafe();
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
            DisposeNativeUnsafe();
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
            throw new InvalidOperationException("The offline recognizer is not initialized.");
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
            DrainSegmentsUnsafe();
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
            DrainSegmentsUnsafe();
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
            DisposeNativeUnsafe();
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
            throw new InvalidOperationException("The offline model descriptor is missing.");
        if (descriptor.StreamingMode is not SpeechStreamingMode.SegmentedVad and
            not SpeechStreamingMode.Offline)
        {
            throw new InvalidOperationException(
                $"{descriptor.DisplayName} is not an offline/VAD model.");
        }

        var selection = await _compute
            .SelectAsync(_settings.Current.DefaultBackend, descriptor, cancellationToken)
            .ConfigureAwait(false);
        var provider = selection.Selected == ComputeBackend.Cuda ? "cuda" : "cpu";
        installation = installation with { Provider = provider };
        var resources = await Task.Run(
                () => CreateResources(installation, descriptor),
                cancellationToken)
            .ConfigureAwait(false);
        _recognizer = resources.Recognizer;
        _vad = resources.Vad;
        ModelInformation = installation.Information;
        _language = _settings.Current.DefaultLanguage;
        _performance.SetSessionContext(
            provider == "cuda" ? "cuda-ready" : "cpu",
            descriptor.Id,
            string.Empty);
        LogOfflineModelLoaded(
            _logger,
            descriptor.Id,
            installation.Engine,
            provider,
            descriptor.VadPolicy?.PreRollMs ?? 200,
            descriptor.VadPolicy?.PostRollMs ?? 500);
    }

    private void DrainSegmentsUnsafe()
    {
        if (_recognizer is null || _vad is null)
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

            using var stream = _recognizer.CreateStream();
            if (stream.HasOption("language"))
            {
                stream.SetOption("language", _language);
            }

            stream.AcceptWaveform(AudioChunk.SampleRate, segment.Samples);
            var stopwatch = Stopwatch.StartNew();
            _recognizer.Decode(stream);
            stopwatch.Stop();
            _performance.RecordDecode(
                stopwatch.Elapsed,
                segment.Samples.Length * 1000d / AudioChunk.SampleRate);
            var text = stream.Result.Text;
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            _performance.RecordRecognitionResult();
            RecognitionResultAvailable?.Invoke(
                this,
                new RecognitionHypothesis(
                    _generation,
                    _sequence,
                    text,
                    true,
                    DateTimeOffset.UtcNow,
                    _language,
                    ModelInformation.Id));
        }
    }

    private static OfflineResources CreateResources(
        ModelInstallation installation,
        ModelDescriptor descriptor)
    {
        var files = installation.Files;
        var model = new OfflineModelConfig
        {
            Tokens = OptionalFile(files, "tokens"),
            NumThreads = installation.NumThreads,
            Provider = installation.Provider,
            Debug = 0,
        };
        switch (installation.Engine.Trim().ToLowerInvariant())
        {
            case "offline-transducer":
            case "offline-parakeet-tdt":
                model.Transducer = new OfflineTransducerModelConfig
                {
                    Encoder = RequiredFile(files, "encoder"),
                    Decoder = RequiredFile(files, "decoder"),
                    Joiner = RequiredFile(files, "joiner"),
                };
                break;
            case "offline-qwen3-asr":
                model.Qwen3Asr = new OfflineQwen3AsrModelConfig
                {
                    ConvFrontend = RequiredFile(files, "conv-frontend"),
                    Encoder = RequiredFile(files, "encoder"),
                    Decoder = RequiredFile(files, "decoder"),
                    Tokenizer = RequiredFile(files, "tokenizer"),
                };
                break;
            case "offline-moonshine":
                model.Moonshine = new OfflineMoonshineModelConfig
                {
                    Preprocessor = RequiredFile(files, "preprocessor"),
                    Encoder = RequiredFile(files, "encoder"),
                    UncachedDecoder = OptionalFile(files, "uncached-decoder"),
                    CachedDecoder = OptionalFile(files, "cached-decoder"),
                    MergedDecoder = OptionalFile(files, "merged-decoder"),
                };
                break;
            default:
                throw new NotSupportedException(
                    $"Offline engine '{installation.Engine}' is not supported.");
        }

        var recognizer = new OfflineRecognizer(new OfflineRecognizerConfig
        {
            FeatConfig = new FeatureConfig
            {
                SampleRate = AudioChunk.SampleRate,
                FeatureDim = installation.FeatureDimension,
            },
            ModelConfig = model,
            DecodingMethod = "greedy_search",
        });
        try
        {
            var policy = descriptor.VadPolicy ?? new VadSegmentationPolicy();
            var vad = new VoiceActivityDetector(
                new VadModelConfig
                {
                    SampleRate = AudioChunk.SampleRate,
                    NumThreads = installation.NumThreads,
                    Provider = installation.Provider,
                    Debug = 0,
                    SileroVad = new SileroVadModelConfig
                    {
                        Model = RequiredFile(files, "vad"),
                        Threshold = policy.Threshold,
                        MinSilenceDuration = policy.PostRollMs / 1000f,
                        MinSpeechDuration = 0.15f,
                        WindowSize = 512,
                        MaxSpeechDuration = policy.MaximumSegmentMs / 1000f,
                    },
                },
                30f);
            return new OfflineResources(recognizer, vad);
        }
        catch
        {
            recognizer.Dispose();
            throw;
        }
    }

    private void DisposeNativeUnsafe()
    {
        _started = false;
        _vad?.Dispose();
        _vad = null;
        _recognizer?.Dispose();
        _recognizer = null;
    }

    private static string RequiredFile(
        IReadOnlyDictionary<string, string> files,
        string key) =>
        files.TryGetValue(key, out var path) && !string.IsNullOrWhiteSpace(path)
            ? path
            : throw new InvalidOperationException(
                $"The selected offline model is missing its required '{key}' artifact.");

    private static string OptionalFile(
        IReadOnlyDictionary<string, string> files,
        string key) =>
        files.TryGetValue(key, out var path) ? path : string.Empty;

    private sealed record OfflineResources(
        OfflineRecognizer Recognizer,
        VoiceActivityDetector Vad);

    [LoggerMessage(
        LogLevel.Information,
        "Loaded offline model {ModelId} using {Engine}, provider {Provider}, VAD pre/post roll {PreRollMs}/{PostRollMs} ms.")]
    private static partial void LogOfflineModelLoaded(
        ILogger logger,
        string modelId,
        string engine,
        string provider,
        int preRollMs,
        int postRollMs);
}
