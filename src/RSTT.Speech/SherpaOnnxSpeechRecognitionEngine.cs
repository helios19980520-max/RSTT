using Microsoft.Extensions.Logging;
using RSTT.Core.Abstractions;
using RSTT.Core.Models;
using SherpaOnnx;

namespace RSTT.Speech;

/// <summary>Local, online sherpa-onnx recognition. All native objects remain owned here.</summary>
public sealed partial class SherpaOnnxSpeechRecognitionEngine : ISpeechRecognitionEngine
{
    private readonly IModelManager _modelManager;
    private readonly ILogger<SherpaOnnxSpeechRecognitionEngine> _logger;
    private readonly SemaphoreSlim _engineLock = new(1, 1);
    private OnlineRecognizer? _recognizer;
    private OnlineStream? _stream;
    private VoiceActivityDetector? _voiceActivityDetector;
    private bool _speechWasActive;
    private bool _isStarted;
    private bool _disposed;

    public SherpaOnnxSpeechRecognitionEngine(IModelManager modelManager, ILogger<SherpaOnnxSpeechRecognitionEngine> logger)
    {
        _modelManager = modelManager;
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

            var installation = _modelManager.GetSelectedInstallation();
            var config = BuildRecognizerConfig(installation);
            _recognizer = new OnlineRecognizer(config);
            _stream = _recognizer.CreateStream();
            _voiceActivityDetector = CreateVoiceActivityDetector(installation);
            ModelInformation = installation.Information;
            LogModelLoaded(_logger, installation.Information.Id, installation.Engine);
        }
        finally
        {
            _engineLock.Release();
        }
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!IsReady)
        {
            throw new InvalidOperationException("The local speech model has not been initialized.");
        }

        _isStarted = true;
        return Task.CompletedTask;
    }

    public async Task ProcessAudioAsync(AudioChunk chunk, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!_isStarted || _recognizer is null || _stream is null || chunk.Samples.Length == 0)
        {
            return;
        }

        await _engineLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var speechDetected = IsSpeechDetected(chunk.Samples);
            if (_voiceActivityDetector is not null)
            {
                _voiceActivityDetector.AcceptWaveform(chunk.Samples);
                speechDetected = _voiceActivityDetector.IsSpeechDetected();
            }

            if (!speechDetected)
            {
                if (_speechWasActive)
                {
                    FlushAndResetUnsafe();
                    _speechWasActive = false;
                }

                return;
            }

            _speechWasActive = true;
            _stream.AcceptWaveform(AudioChunk.SampleRate, chunk.Samples);
            while (_recognizer.IsReady(_stream))
            {
                _recognizer.Decode(_stream);
            }

            var result = _recognizer.GetResult(_stream);
            var isFinal = _recognizer.IsEndpoint(_stream);
            if (!string.IsNullOrWhiteSpace(result.Text))
            {
                RecognitionResultAvailable?.Invoke(this, new RecognitionResult(result.Text, isFinal, chunk.SequenceNumber, DateTimeOffset.UtcNow));
            }

            if (isFinal)
            {
                _recognizer.Reset(_stream);
            }
        }
        finally
        {
            _engineLock.Release();
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
            FlushAndResetUnsafe();
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
            FlushAndResetUnsafe();
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
            _stream?.Dispose();
            _stream = null;
            _recognizer?.Dispose();
            _recognizer = null;
            _voiceActivityDetector?.Dispose();
            _voiceActivityDetector = null;
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
            FeatConfig = new FeatureConfig { SampleRate = AudioChunk.SampleRate, FeatureDim = 80 },
            ModelConfig = modelConfig,
            DecodingMethod = "greedy_search",
            MaxActivePaths = 4,
            EnableEndpoint = 1,
            Rule1MinTrailingSilence = 2.4f,
            Rule2MinTrailingSilence = 1.2f,
            Rule3MinUtteranceLength = 20f,
        };
    }

    private void FlushAndResetUnsafe()
    {
        if (_recognizer is null || _stream is null)
        {
            return;
        }

        _stream.InputFinished();
        while (_recognizer.IsReady(_stream))
        {
            _recognizer.Decode(_stream);
        }

        var result = _recognizer.GetResult(_stream);
        if (!string.IsNullOrWhiteSpace(result.Text))
        {
            RecognitionResultAvailable?.Invoke(this, new RecognitionResult(result.Text, true, 0, DateTimeOffset.UtcNow));
        }

        _recognizer.Reset(_stream);
        _voiceActivityDetector?.Reset();
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

    private static bool IsSpeechDetected(float[] samples)
    {
        if (samples.Length == 0)
        {
            return false;
        }

        double sum = 0;
        foreach (var sample in samples)
        {
            sum += sample * sample;
        }

        return Math.Sqrt(sum / samples.Length) >= 0.008;
    }

    [LoggerMessage(LogLevel.Information, "Loaded local sherpa-onnx model {ModelId} using {Engine}.")]
    private static partial void LogModelLoaded(ILogger logger, string modelId, string engine);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
