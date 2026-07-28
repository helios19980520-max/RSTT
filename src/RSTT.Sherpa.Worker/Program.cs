using System.Diagnostics;
using System.IO.Pipes;
using RSTT.Core.Workers;
using SherpaOnnx;

#if SHERPA_CUDA12
const string Backend = "cuda";
#else
const string Backend = "cpu";
#endif

return await RunAsync(args).ConfigureAwait(false);

static async Task<int> RunAsync(string[] arguments)
{
    var pipeName = GetArgument(arguments, "--pipe");
    if (string.IsNullOrWhiteSpace(pipeName))
    {
        Console.Error.WriteLine("Usage: RSTT.Speech.Worker --pipe <name>");
        return 2;
    }

    await using var pipe = new NamedPipeServerStream(
        pipeName,
        PipeDirection.InOut,
        1,
        PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | PipeOptions.WriteThrough);
    await pipe.WaitForConnectionAsync().ConfigureAwait(false);
    using var runtime = new SherpaRuntime(Backend);
    while (pipe.IsConnected)
    {
        var frame = await SpeechWorkerProtocol.ReadAsync(pipe).ConfigureAwait(false);
        if (frame is null)
        {
            break;
        }

        try
        {
            switch (frame.Type)
            {
                case SpeechWorkerMessageType.Handshake:
                    _ = SpeechWorkerProtocol.DeserializeJson<WorkerHandshakeRequest>(frame);
                    await ReplyJsonAsync(
                        pipe,
                        SpeechWorkerMessageType.Ready,
                        frame.CorrelationId,
                        new WorkerHandshakeResponse(
                            SpeechWorkerProtocol.Version,
                            typeof(OnlineRecognizer).Assembly.GetName().Version?.ToString() ?? "unknown",
                            "sherpa-onnx",
                            "sherpa-onnx 1.13.4",
                            Backend)).ConfigureAwait(false);
                    break;
                case SpeechWorkerMessageType.Load:
                    runtime.Load(SpeechWorkerProtocol.DeserializeJson<WorkerLoadRequest>(frame));
                    await ReadyAsync(pipe, frame.CorrelationId, "load", runtime.Engine).ConfigureAwait(false);
                    break;
                case SpeechWorkerMessageType.Warmup:
                {
                    var performance = runtime.Warmup();
                    await ReplyJsonAsync(
                        pipe,
                        SpeechWorkerMessageType.Performance,
                        frame.CorrelationId,
                        performance).ConfigureAwait(false);
                    await ReadyAsync(pipe, frame.CorrelationId, "warmup", Backend).ConfigureAwait(false);
                    break;
                }
                case SpeechWorkerMessageType.Start:
                    runtime.Start(SpeechWorkerProtocol.DeserializeJson<WorkerStartRequest>(frame));
                    await ReadyAsync(pipe, frame.CorrelationId, "start", "accepting audio").ConfigureAwait(false);
                    break;
                case SpeechWorkerMessageType.Audio:
                {
                    var result = runtime.AcceptAudio(
                        frame.CorrelationId,
                        SpeechWorkerProtocol.DeserializeAudio(frame));
                    await ReplyBatchAsync(pipe, frame.CorrelationId, result).ConfigureAwait(false);
                    break;
                }
                case SpeechWorkerMessageType.Finish:
                {
                    var result = runtime.Finish();
                    await ReplyBatchAsync(pipe, frame.CorrelationId, result).ConfigureAwait(false);
                    break;
                }
                case SpeechWorkerMessageType.Unload:
                    runtime.Unload();
                    await ReadyAsync(pipe, frame.CorrelationId, "unload", "complete").ConfigureAwait(false);
                    break;
                case SpeechWorkerMessageType.Ping:
                    await ReplyJsonAsync(
                        pipe,
                        SpeechWorkerMessageType.Pong,
                        frame.CorrelationId,
                        new WorkerReadyResponse("ping", "ok")).ConfigureAwait(false);
                    break;
                case SpeechWorkerMessageType.Shutdown:
                    runtime.Unload();
                    await ReadyAsync(pipe, frame.CorrelationId, "shutdown", "complete").ConfigureAwait(false);
                    return 0;
                default:
                    throw new InvalidDataException($"Unexpected request {frame.Type}.");
            }
        }
        catch (Exception exception)
        {
            await ReplyJsonAsync(
                pipe,
                SpeechWorkerMessageType.Fault,
                frame.CorrelationId,
                new WorkerFaultResponse(
                    frame.Type.ToString(),
                    exception.GetType().Name,
                    exception.Message,
                    false)).ConfigureAwait(false);
        }
    }

    return 0;
}

static async Task ReplyBatchAsync(Stream pipe, long correlationId, WorkerBatch result)
{
    foreach (var hypothesis in result.Hypotheses)
    {
        await ReplyJsonAsync(
            pipe,
            SpeechWorkerMessageType.Hypothesis,
            correlationId,
            hypothesis).ConfigureAwait(false);
    }

    await ReplyJsonAsync(
        pipe,
        SpeechWorkerMessageType.Performance,
        correlationId,
        result.Performance).ConfigureAwait(false);
    await ReadyAsync(pipe, correlationId, result.Stage, "complete").ConfigureAwait(false);
}

static string? GetArgument(string[] arguments, string name)
{
    for (var index = 0; index < arguments.Length - 1; index++)
    {
        if (string.Equals(arguments[index], name, StringComparison.OrdinalIgnoreCase))
        {
            return arguments[index + 1];
        }
    }

    return null;
}

static Task ReadyAsync(Stream stream, long correlationId, string stage, string detail) =>
    ReplyJsonAsync(
        stream,
        SpeechWorkerMessageType.Ready,
        correlationId,
        new WorkerReadyResponse(stage, detail));

static async Task ReplyJsonAsync<T>(
    Stream stream,
    SpeechWorkerMessageType type,
    long correlationId,
    T response) =>
    await SpeechWorkerProtocol.WriteAsync(
        stream,
        SpeechWorkerProtocol.Json(type, correlationId, response)).ConfigureAwait(false);

internal sealed class SherpaRuntime : IDisposable
{
    private const int SampleRate = 16_000;
    private readonly string _backend;
    private OnlineRecognizer? _online;
    private OnlineStream? _onlineStream;
    private OfflineRecognizer? _offline;
    private VoiceActivityDetector? _vad;
    private VadAudioBuffer? _vadAudioBuffer;
    private WorkerLoadRequest? _load;
    private long _generation;
    private long _sequence;
    private string _lastText = string.Empty;
    private double _audioMilliseconds;
    private bool _started;

    public SherpaRuntime(string backend)
    {
        _backend = backend;
    }

    public string Engine => _load?.Engine ?? string.Empty;

    public void Load(WorkerLoadRequest request)
    {
        Unload();
        if (request.Files is null || request.Files.Count == 0)
        {
            throw new InvalidDataException("The sherpa worker load request has no model files.");
        }

        foreach (var (key, path) in request.Files)
        {
            if (key.Equals("tokenizer", StringComparison.OrdinalIgnoreCase))
            {
                if (!Directory.Exists(path))
                {
                    throw new DirectoryNotFoundException(
                        $"The required tokenizer directory does not exist: {path}");
                }
            }
            else if (!File.Exists(path))
            {
                throw new FileNotFoundException($"The required model file '{key}' is missing.", path);
            }
        }

        _load = request;
        if (IsOnline(request.Engine))
        {
            _online = new OnlineRecognizer(BuildOnlineConfig(request));
        }
        else
        {
            _offline = new OfflineRecognizer(BuildOfflineConfig(request));
            _vad = CreateVad(request);
        }
    }

    public WorkerPerformanceResponse Warmup()
    {
        EnsureLoaded();
        var samples = new float[SampleRate];
        var stopwatch = Stopwatch.StartNew();
        if (_online is not null)
        {
            using var stream = _online.CreateStream();
            ConfigureLanguage(stream);
            stream.AcceptWaveform(SampleRate, samples);
            stream.InputFinished();
            while (_online.IsReady(stream))
            {
                _online.Decode(stream);
            }

            _ = _online.GetResult(stream).Text;
        }
        else
        {
            using var stream = _offline!.CreateStream();
            ConfigureLanguage(stream);
            stream.AcceptWaveform(SampleRate, samples);
            _offline.Decode(stream);
            _ = stream.Result.Text;
        }

        stopwatch.Stop();
        return new WorkerPerformanceResponse(
            1_000,
            stopwatch.Elapsed.TotalMilliseconds,
            Environment.WorkingSet);
    }

    public void Start(WorkerStartRequest request)
    {
        EnsureLoaded();
        _generation = request.SessionGenerationId;
        _sequence = request.SequenceId;
        _lastText = string.Empty;
        _audioMilliseconds = 0;
        _started = true;
        if (_online is not null)
        {
            _onlineStream?.Dispose();
            _onlineStream = _online.CreateStream();
            ConfigureLanguage(_onlineStream);
        }
        else
        {
            _vadAudioBuffer!.Reset();
            _vad!.Reset();
        }
    }

    public WorkerBatch AcceptAudio(long sequence, float[] samples)
    {
        EnsureStarted();
        _sequence = sequence;
        _audioMilliseconds += samples.Length * 1000d / SampleRate;
        return _online is not null
            ? AcceptOnline(samples)
            : AcceptOffline(samples, false);
    }

    public WorkerBatch Finish()
    {
        EnsureStarted();
        _started = false;
        return _online is not null
            ? FinishOnline()
            : AcceptOffline([], true);
    }

    public void Unload()
    {
        _started = false;
        _onlineStream?.Dispose();
        _onlineStream = null;
        _online?.Dispose();
        _online = null;
        _vad?.Dispose();
        _vad = null;
        _vadAudioBuffer = null;
        _offline?.Dispose();
        _offline = null;
        _load = null;
        _lastText = string.Empty;
        _audioMilliseconds = 0;
    }

    public void Dispose() => Unload();

    private WorkerBatch AcceptOnline(float[] samples)
    {
        var hypotheses = new List<WorkerHypothesisResponse>();
        var stopwatch = Stopwatch.StartNew();
        _onlineStream!.AcceptWaveform(SampleRate, samples);
        while (_online!.IsReady(_onlineStream))
        {
            _online.Decode(_onlineStream);
        }

        var result = _online.GetResult(_onlineStream).Text;
        var endpoint = _online.IsEndpoint(_onlineStream);
        if (!string.IsNullOrWhiteSpace(result) &&
            (!string.Equals(result, _lastText, StringComparison.Ordinal) || endpoint))
        {
            hypotheses.Add(CreateHypothesis(result, endpoint));
            _lastText = result;
        }

        if (endpoint)
        {
            _online.Reset(_onlineStream);
            _lastText = string.Empty;
        }

        stopwatch.Stop();
        return new WorkerBatch(
            "audio",
            hypotheses,
            Performance(stopwatch.Elapsed.TotalMilliseconds));
    }

    private WorkerBatch FinishOnline()
    {
        var hypotheses = new List<WorkerHypothesisResponse>();
        var stopwatch = Stopwatch.StartNew();
        _onlineStream!.InputFinished();
        while (_online!.IsReady(_onlineStream))
        {
            _online.Decode(_onlineStream);
        }

        var result = _online.GetResult(_onlineStream).Text;
        if (!string.IsNullOrWhiteSpace(result))
        {
            hypotheses.Add(CreateHypothesis(result, true));
        }

        stopwatch.Stop();
        _onlineStream.Dispose();
        _onlineStream = null;
        _lastText = string.Empty;
        return new WorkerBatch(
            "finish",
            hypotheses,
            Performance(stopwatch.Elapsed.TotalMilliseconds));
    }

    private WorkerBatch AcceptOffline(float[] samples, bool finish)
    {
        var hypotheses = new List<WorkerHypothesisResponse>();
        var decodeMilliseconds = 0d;
        var decodedAudioMilliseconds = 0d;
        var segmentCount = 0;
        if (samples.Length > 0)
        {
            _vadAudioBuffer!.Append(samples);
            _vad!.AcceptWaveform(samples);
        }

        if (finish)
        {
            _vad!.Flush();
        }

        while (!_vad!.IsEmpty())
        {
            var segment = _vad.Front();
            _vad.Pop();
            if (segment.Samples.Length == 0)
            {
                continue;
            }

            var contextualSamples = _vadAudioBuffer!.Extract(
                segment.Start,
                segment.Samples.Length);
            if (contextualSamples.Length == 0)
            {
                contextualSamples = segment.Samples;
            }

            decodedAudioMilliseconds += contextualSamples.Length * 1000d / SampleRate;
            segmentCount++;
            using var stream = _offline!.CreateStream();
            ConfigureLanguage(stream);
            stream.AcceptWaveform(SampleRate, contextualSamples);
            var stopwatch = Stopwatch.StartNew();
            _offline.Decode(stream);
            stopwatch.Stop();
            decodeMilliseconds += stopwatch.Elapsed.TotalMilliseconds;
            var text = stream.Result.Text;
            if (!string.IsNullOrWhiteSpace(text))
            {
                hypotheses.Add(CreateHypothesis(text, true));
            }
        }

        if (finish)
        {
            _vad.Reset();
        }

        return new WorkerBatch(
            finish ? "finish" : "audio",
            hypotheses,
            Performance(
                decodeMilliseconds,
                decodedAudioMilliseconds,
                segmentCount));
    }

    private WorkerHypothesisResponse CreateHypothesis(string text, bool isFinal) =>
        new(
            _generation,
            _sequence,
            text,
            NormalizeLanguage(_load!.Language),
            isFinal);

    private WorkerPerformanceResponse Performance(
        double decodeMilliseconds,
        double decodedAudioMilliseconds = 0,
        int segmentCount = 0)
    {
        var result = new WorkerPerformanceResponse(
            _audioMilliseconds,
            decodeMilliseconds,
            Environment.WorkingSet,
            decodedAudioMilliseconds,
            segmentCount);
        _audioMilliseconds = 0;
        return result;
    }

    private OnlineRecognizerConfig BuildOnlineConfig(WorkerLoadRequest request)
    {
        var files = request.Files!;
        var model = new OnlineModelConfig
        {
            Tokens = RequiredFile(files, "tokens"),
            NumThreads = Math.Clamp(request.Threads, 1, 16),
            Provider = _backend,
            Debug = 0,
        };
        switch (request.Engine.Trim().ToLowerInvariant())
        {
            case "online-transducer":
                model.Transducer = new OnlineTransducerModelConfig
                {
                    Encoder = RequiredFile(files, "encoder"),
                    Decoder = RequiredFile(files, "decoder"),
                    Joiner = RequiredFile(files, "joiner"),
                };
                break;
            case "online-zipformer2-ctc":
                model.Zipformer2Ctc = new OnlineZipformer2CtcModelConfig
                {
                    Model = RequiredFile(files, "model"),
                };
                break;
            case "online-nemo-ctc":
                model.NemoCtc = new OnlineNemoCtcModelConfig
                {
                    Model = RequiredFile(files, "model"),
                };
                break;
            default:
                throw new NotSupportedException(
                    $"Online engine '{request.Engine}' is not supported.");
        }

        return new OnlineRecognizerConfig
        {
            FeatConfig = new FeatureConfig
            {
                SampleRate = SampleRate,
                FeatureDim = request.FeatureDimension,
            },
            ModelConfig = model,
            DecodingMethod = "greedy_search",
            MaxActivePaths = 4,
            EnableEndpoint = 1,
            Rule1MinTrailingSilence = 2.4f,
            Rule2MinTrailingSilence = 1.2f,
            Rule3MinUtteranceLength = 20f,
        };
    }

    private OfflineRecognizerConfig BuildOfflineConfig(WorkerLoadRequest request)
    {
        var files = request.Files!;
        var model = new OfflineModelConfig
        {
            Tokens = OptionalFile(files, "tokens"),
            NumThreads = Math.Clamp(request.Threads, 1, 16),
            Provider = _backend,
            Debug = 0,
        };
        switch (request.Engine.Trim().ToLowerInvariant())
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
                    Tokenizer = RequiredDirectoryFromFile(
                        files,
                        "tokenizer-merges"),
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
                    $"Offline engine '{request.Engine}' is not supported.");
        }

        return new OfflineRecognizerConfig
        {
            FeatConfig = new FeatureConfig
            {
                SampleRate = SampleRate,
                FeatureDim = request.FeatureDimension,
            },
            ModelConfig = model,
            DecodingMethod = "greedy_search",
        };
    }

    private VoiceActivityDetector CreateVad(WorkerLoadRequest request)
    {
        var policy = request.VadPolicy ?? new WorkerVadPolicy();
        _vadAudioBuffer = new VadAudioBuffer(
            SampleRate,
            policy.PreRollMs,
            policy.PostRollMs,
            policy.MaximumSegmentMs);
        return new VoiceActivityDetector(
            new VadModelConfig
            {
                SampleRate = SampleRate,
                NumThreads = Math.Clamp(request.Threads, 1, 16),
                Provider = _backend,
                Debug = 0,
                SileroVad = new SileroVadModelConfig
                {
                    Model = RequiredFile(request.Files!, "vad"),
                    Threshold = policy.Threshold,
                    MinSilenceDuration = policy.PostRollMs / 1000f,
                    MinSpeechDuration = 0.15f,
                    WindowSize = 512,
                    MaxSpeechDuration = policy.MaximumSegmentMs / 1000f,
                },
            },
            30f);
    }

    private void ConfigureLanguage(OnlineStream stream)
    {
        var language = NormalizeLanguage(_load!.Language);
        if (stream.HasOption("language"))
        {
            stream.SetOption("language", language);
        }
    }

    private void ConfigureLanguage(OfflineStream stream)
    {
        var language = NormalizeLanguage(_load!.Language);
        if (stream.HasOption("language"))
        {
            stream.SetOption("language", language);
        }
    }

    private void EnsureLoaded()
    {
        if (_online is null && _offline is null)
        {
            throw new InvalidOperationException("Load a model before recognition.");
        }
    }

    private void EnsureStarted()
    {
        EnsureLoaded();
        if (!_started)
        {
            throw new InvalidOperationException("Start a session before sending audio.");
        }
    }

    private static bool IsOnline(string engine) =>
        engine.StartsWith("online-", StringComparison.OrdinalIgnoreCase);

    private static string RequiredFile(
        IReadOnlyDictionary<string, string> files,
        string key) =>
        files.TryGetValue(key, out var path) && !string.IsNullOrWhiteSpace(path)
            ? path
            : throw new InvalidDataException($"Required model artifact '{key}' is missing.");

    private static string OptionalFile(
        IReadOnlyDictionary<string, string> files,
        string key) =>
        files.TryGetValue(key, out var path) ? path : string.Empty;

    private static string RequiredDirectoryFromFile(
        IReadOnlyDictionary<string, string> files,
        string key)
    {
        var file = RequiredFile(files, key);
        return Path.GetDirectoryName(file) ??
            throw new InvalidDataException(
                $"Required model artifact '{key}' has no parent directory.");
    }

    private static string NormalizeLanguage(string value) =>
        string.IsNullOrWhiteSpace(value) ? "auto" : value.Trim().ToLowerInvariant();
}

internal sealed record WorkerBatch(
    string Stage,
    IReadOnlyList<WorkerHypothesisResponse> Hypotheses,
    WorkerPerformanceResponse Performance);
