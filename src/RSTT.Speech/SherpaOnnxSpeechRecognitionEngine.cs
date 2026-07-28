using System.Diagnostics;
using Microsoft.Extensions.Logging;
using RSTT.Core.Abstractions;
using RSTT.Core.Models;
using SherpaOnnx;

namespace RSTT.Speech;

/// <summary>Local, online sherpa-onnx recognition. All native objects remain owned here.</summary>
public sealed partial class SherpaOnnxSpeechRecognitionEngine : ISpeechRecognitionEngine
{
    private readonly IModelManager _modelManager;
    private readonly ISettingsService _settings;
    private readonly IComputeDeviceService _computeDevices;
    private readonly IPerformanceMonitor _performance;
    private readonly ILogger<SherpaOnnxSpeechRecognitionEngine> _logger;
    private readonly SemaphoreSlim _engineLock = new(1, 1);
    private OnlineRecognizer? _recognizer;
    private OnlineStream? _stream;
    private VoiceActivityDetector? _voiceActivityDetector;
    private long _lastSequenceNumber;
    private double _audioSinceDecodeMilliseconds;
    private string _lastPublishedText = string.Empty;
    private string _recognitionLanguage = "en";
    private bool _streamHasAudio;
    private bool _isStarted;
    private bool _disposed;

    public SherpaOnnxSpeechRecognitionEngine(
        IModelManager modelManager,
        ISettingsService settings,
        IComputeDeviceService computeDevices,
        IPerformanceMonitor performance,
        ILogger<SherpaOnnxSpeechRecognitionEngine> logger)
    {
        _modelManager = modelManager;
        _settings = settings;
        _computeDevices = computeDevices;
        _performance = performance;
        _logger = logger;
        ModelInformation = modelManager.GetSelectedModel();
    }

    public bool IsReady => _recognizer is not null;

    public ModelInformation ModelInformation { get; private set; }

    public event EventHandler<RecognitionResult>? RecognitionResultAvailable;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _engineLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_recognizer is not null)
            {
                return;
            }

            await LoadModelUnsafeAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _engineLock.Release();
        }
    }

    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _engineLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _isStarted = false;
            DisposeNativeUnsafe();
            await LoadModelUnsafeAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _engineLock.Release();
        }
    }

    public async Task UnloadAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _engineLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _isStarted = false;
            DisposeNativeUnsafe();
            ModelInformation = _modelManager.GetSelectedModel();
        }
        finally
        {
            _engineLock.Release();
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _engineLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_recognizer is null)
            {
                throw new InvalidOperationException("The local speech model has not been initialized.");
            }

            _stream ??= _recognizer.CreateStream();
            _isStarted = true;
        }
        finally
        {
            _engineLock.Release();
        }
    }

    public async Task ProcessAudioAsync(AudioChunk chunk, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!_isStarted || _recognizer is null || _stream is null || chunk.Samples.Length == 0)
        {
            return;
        }

        RecognitionResult? publishedResult = null;
        await _engineLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _lastSequenceNumber = chunk.SequenceNumber;
            if (_voiceActivityDetector is not null)
            {
                _voiceActivityDetector.AcceptWaveform(chunk.Samples);
            }

            // Online models require the silence around speech for endpoint detection.
            // Feeding every normalized chunk also preserves pre-roll before a VAD fires.
            _stream.AcceptWaveform(AudioChunk.SampleRate, chunk.Samples);
            _streamHasAudio = true;
            _audioSinceDecodeMilliseconds += chunk.Duration.TotalMilliseconds;
            var decodeCount = 0;
            var decodeStopwatch = Stopwatch.StartNew();
            while (_recognizer.IsReady(_stream))
            {
                _recognizer.Decode(_stream);
                decodeCount++;
            }
            decodeStopwatch.Stop();
            if (decodeCount > 0)
            {
                _performance.RecordDecode(
                    decodeStopwatch.Elapsed,
                    _audioSinceDecodeMilliseconds);
                _audioSinceDecodeMilliseconds = 0;
            }

            var result = _recognizer.GetResult(_stream);
            var isFinal = _recognizer.IsEndpoint(_stream);
            if (!string.IsNullOrWhiteSpace(result.Text) &&
                (isFinal || !string.Equals(result.Text, _lastPublishedText, StringComparison.Ordinal)))
            {
                _lastPublishedText = result.Text;
                publishedResult = new RecognitionResult(
                    result.Text,
                    isFinal,
                    chunk.SequenceNumber,
                    DateTimeOffset.UtcNow,
                    Language: _recognitionLanguage);
            }

            if (isFinal)
            {
                _recognizer.Reset(_stream);
                _streamHasAudio = false;
                _lastPublishedText = string.Empty;
            }
        }
        finally
        {
            _engineLock.Release();
        }

        if (publishedResult is not null)
        {
            _performance.RecordRecognitionResult();
            RecognitionResultAvailable?.Invoke(this, publishedResult);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return;
        }

        await _engineLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _isStarted = false;
            FinalizeAndCreateFreshStreamUnsafe();
        }
        finally
        {
            _engineLock.Release();
        }
    }

    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _engineLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            FinalizeAndCreateFreshStreamUnsafe();
        }
        finally
        {
            _engineLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _engineLock.WaitAsync().ConfigureAwait(false);
        try
        {
            _disposed = true;
            _isStarted = false;
            DisposeNativeUnsafe();
        }
        finally
        {
            _engineLock.Release();
            _engineLock.Dispose();
        }
    }

    private static OnlineRecognizerConfig BuildRecognizerConfig(ModelInstallation installation)
    {
        var files = installation.Files;
        var modelConfig = new OnlineModelConfig
        {
            Tokens = RequiredFile(files, "tokens"),
            NumThreads = installation.NumThreads,
            Provider = installation.Provider,
            Debug = 0,
        };

        switch (installation.Engine.Trim().ToLowerInvariant())
        {
            case "online-transducer":
                modelConfig.Transducer = new OnlineTransducerModelConfig
                {
                    Encoder = RequiredFile(files, "encoder"),
                    Decoder = RequiredFile(files, "decoder"),
                    Joiner = RequiredFile(files, "joiner"),
                };
                break;
            case "online-zipformer2-ctc":
                modelConfig.Zipformer2Ctc = new OnlineZipformer2CtcModelConfig { Model = RequiredFile(files, "model") };
                break;
            case "online-nemo-ctc":
                modelConfig.NemoCtc = new OnlineNemoCtcModelConfig { Model = RequiredFile(files, "model") };
                break;
            default:
                throw new NotSupportedException($"The model engine '{installation.Engine}' is not supported by RSTT V1.");
        }

        return new OnlineRecognizerConfig
        {
            FeatConfig = new FeatureConfig { SampleRate = AudioChunk.SampleRate, FeatureDim = installation.FeatureDimension },
            ModelConfig = modelConfig,
            DecodingMethod = "greedy_search",
            MaxActivePaths = 4,
            EnableEndpoint = 1,
            Rule1MinTrailingSilence = 2.4f,
            Rule2MinTrailingSilence = 1.2f,
            Rule3MinUtteranceLength = 20f,
        };
    }

    private async Task LoadModelUnsafeAsync(CancellationToken cancellationToken)
    {
        var installation = _modelManager.GetSelectedInstallation();
        var descriptor = installation.Descriptor ?? new ModelDescriptor
        {
            Id = installation.Information.Id,
            DisplayName = installation.Information.DisplayName,
            CpuSupported = true,
        };
        var compute = await _computeDevices
            .SelectAsync(_settings.Current.ComputeBackend, descriptor, cancellationToken)
            .ConfigureAwait(false);
        var profile = SelectProfile(descriptor.LatencyProfiles, _settings.Current.RecognitionMode)
            ?? installation.RecognitionProfile;
        var provider = compute.Selected == ComputeBackend.Cuda ? "cuda" : "cpu";
        var threads = ResolveThreadCount(installation, profile, provider);
        installation = installation with
        {
            Provider = provider,
            NumThreads = threads,
            RecognitionProfile = profile,
        };
        var resources = await Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var recognizer = new OnlineRecognizer(BuildRecognizerConfig(installation));
                try
                {
                    var stream = recognizer.CreateStream();
                    ConfigureStreamLanguage(stream, descriptor, _settings.Current.Language);
                    var vad = CreateVoiceActivityDetector(installation);
                    return new EngineResources(recognizer, stream, vad);
                }
                catch
                {
                    recognizer.Dispose();
                    throw;
                }
            },
            cancellationToken).ConfigureAwait(false);

        _recognizer = resources.Recognizer;
        _stream = resources.Stream;
        _voiceActivityDetector = resources.VoiceActivityDetector;
        _streamHasAudio = false;
        _lastSequenceNumber = 0;
        _audioSinceDecodeMilliseconds = 0;
        _lastPublishedText = string.Empty;
        _recognitionLanguage = ResolveLanguage(descriptor, _settings.Current.Language);
        ModelInformation = installation.Information;
        _performance.SetSessionContext(provider, installation.Information.Id, string.Empty);
        LogModelLoaded(
            _logger,
            installation.Information.Id,
            installation.Engine,
            provider,
            threads,
            profile?.DisplayName ?? "default",
            compute.Reason);
    }

    private void FinalizeAndCreateFreshStreamUnsafe()
    {
        if (_recognizer is null || _stream is null)
        {
            return;
        }

        if (_streamHasAudio)
        {
            _stream.InputFinished();
            var decodeStopwatch = Stopwatch.StartNew();
            while (_recognizer.IsReady(_stream))
            {
                _recognizer.Decode(_stream);
            }
            decodeStopwatch.Stop();
            _performance.RecordDecode(
                decodeStopwatch.Elapsed,
                _audioSinceDecodeMilliseconds);
            _audioSinceDecodeMilliseconds = 0;

            var result = _recognizer.GetResult(_stream);
            if (!string.IsNullOrWhiteSpace(result.Text))
            {
                _performance.RecordRecognitionResult();
                RecognitionResultAvailable?.Invoke(
                    this,
                    new RecognitionResult(
                        result.Text,
                        true,
                        _lastSequenceNumber,
                        DateTimeOffset.UtcNow,
                        Language: _recognitionLanguage));
            }
        }

        // InputFinished is terminal for an OnlineStream. Dispose it and create a new
        // stream instead of resetting and reusing the finalized native handle.
        _stream.Dispose();
        _stream = _recognizer.CreateStream();
        if (ModelInformation.Descriptor is { } descriptor)
        {
            ConfigureStreamLanguage(_stream, descriptor, _settings.Current.Language);
        }
        _streamHasAudio = false;
        _lastPublishedText = string.Empty;
        _voiceActivityDetector?.Reset();
    }

    private void DisposeNativeUnsafe()
    {
        _stream?.Dispose();
        _stream = null;
        _recognizer?.Dispose();
        _recognizer = null;
        _voiceActivityDetector?.Dispose();
        _voiceActivityDetector = null;
        _streamHasAudio = false;
        _lastSequenceNumber = 0;
        _audioSinceDecodeMilliseconds = 0;
        _lastPublishedText = string.Empty;
    }

    private int ResolveThreadCount(
        ModelInstallation installation,
        StreamingRecognitionProfile? profile,
        string provider)
    {
        if (!string.Equals(provider, "cpu", StringComparison.OrdinalIgnoreCase))
        {
            return Math.Clamp(installation.NumThreads, 1, 4);
        }

        if (_settings.Current.CpuThreadLimit > 0)
        {
            return Math.Clamp(_settings.Current.CpuThreadLimit, 1, Math.Min(8, Environment.ProcessorCount));
        }

        var recommended = profile?.RecommendedThreads ?? installation.NumThreads;
        return _settings.Current.RecognitionMode switch
        {
            RecognitionMode.LowPower => 1,
            RecognitionMode.LowLatency => Math.Clamp(Math.Max(recommended, 2), 1, Math.Min(4, Environment.ProcessorCount)),
            _ => Math.Clamp(recommended, 1, Math.Min(4, Environment.ProcessorCount)),
        };
    }

    private static StreamingRecognitionProfile? SelectProfile(
        IReadOnlyList<StreamingRecognitionProfile> profiles,
        RecognitionMode mode)
    {
        if (profiles.Count == 0)
        {
            return null;
        }

        return mode switch
        {
            RecognitionMode.LowLatency => profiles.MinBy(profile => profile.ExpectedLatencyMs),
            RecognitionMode.LowPower or RecognitionMode.Accuracy =>
                profiles.MaxBy(profile => profile.ExpectedLatencyMs),
            _ => profiles.FirstOrDefault(profile =>
                    profile.Id.Contains("balanced", StringComparison.OrdinalIgnoreCase))
                ?? profiles[profiles.Count / 2],
        };
    }

    private static void ConfigureStreamLanguage(
        OnlineStream stream,
        ModelDescriptor descriptor,
        string configuredLanguage)
    {
        if (!descriptor.Capabilities.SupportsLanguageDetection)
        {
            return;
        }

        stream.SetOption("language", ResolveLanguage(descriptor, configuredLanguage));
    }

    private static string ResolveLanguage(ModelDescriptor descriptor, string configuredLanguage)
    {
        if (!descriptor.Capabilities.SupportsLanguageDetection)
        {
            return descriptor.Languages.Count > 0
                ? descriptor.Languages[0]
                : configuredLanguage;
        }

        if (string.IsNullOrWhiteSpace(configuredLanguage) ||
            configuredLanguage.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return "auto";
        }

        var exact = descriptor.Languages.FirstOrDefault(language =>
            language.Equals(configuredLanguage, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact;
        }

        var neutral = configuredLanguage.Split('-', StringSplitOptions.RemoveEmptyEntries)[0];
        return descriptor.Languages.FirstOrDefault(language =>
                language.StartsWith(neutral + "-", StringComparison.OrdinalIgnoreCase))
            ?? "auto";
    }

    private static string RequiredFile(IReadOnlyDictionary<string, string> files, string name) =>
        files.TryGetValue(name, out var path)
            ? path
            : throw new InvalidOperationException($"The selected model is missing the required '{name}' file declaration.");

    private static VoiceActivityDetector? CreateVoiceActivityDetector(ModelInstallation installation)
    {
        if (!installation.Files.TryGetValue("vad", out var vadModelPath))
        {
            return null;
        }

        var config = new VadModelConfig
        {
            SampleRate = AudioChunk.SampleRate,
            NumThreads = installation.NumThreads,
            Provider = installation.Provider,
            Debug = 0,
            SileroVad = new SileroVadModelConfig
            {
                Model = vadModelPath,
                Threshold = 0.5f,
                MinSilenceDuration = 0.5f,
                MinSpeechDuration = 0.15f,
                WindowSize = 512,
                MaxSpeechDuration = 20f,
            },
        };
        return new VoiceActivityDetector(config, 30f);
    }

    private sealed record EngineResources(
        OnlineRecognizer Recognizer,
        OnlineStream Stream,
        VoiceActivityDetector? VoiceActivityDetector);

    [LoggerMessage(
        LogLevel.Information,
        "Loaded local sherpa-onnx model {ModelId} using {Engine}, provider {Provider}, {ThreadCount} thread(s), profile {Profile}. Compute selection: {ComputeReason}")]
    private static partial void LogModelLoaded(
        ILogger logger,
        string modelId,
        string engine,
        string provider,
        int threadCount,
        string profile,
        string computeReason);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
