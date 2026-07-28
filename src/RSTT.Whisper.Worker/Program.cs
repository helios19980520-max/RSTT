using System.Diagnostics;
using System.IO.Pipes;
using RSTT.Core.Workers;
using SherpaOnnx;
using Whisper.net;
#if WHISPER_CUDA12
using Whisper.net.LibraryLoader;
#endif

#if WHISPER_CUDA12
RuntimeOptions.RuntimeLibraryOrder = [RuntimeLibrary.Cuda12];
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
        Console.Error.WriteLine("Usage: RSTT.Whisper.Worker --pipe <name>");
        return 2;
    }

    await using var pipe = new NamedPipeServerStream(
        pipeName,
        PipeDirection.InOut,
        1,
        PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | PipeOptions.WriteThrough);
    await pipe.WaitForConnectionAsync().ConfigureAwait(false);
    await using var runtime = new WhisperRuntime(Backend);
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
                            typeof(WhisperFactory).Assembly.GetName().Version?.ToString() ?? "unknown",
                            "whisper.cpp",
                            "Whisper.net 1.9.1",
                            Backend)).ConfigureAwait(false);
                    break;
                case SpeechWorkerMessageType.Load:
                    runtime.Load(SpeechWorkerProtocol.DeserializeJson<WorkerLoadRequest>(frame));
                    await ReadyAsync(pipe, frame.CorrelationId, "load", Backend).ConfigureAwait(false);
                    break;
                case SpeechWorkerMessageType.Warmup:
                {
                    var performance = await runtime.WarmupAsync().ConfigureAwait(false);
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
                    var batch = await runtime
                        .AcceptAudioAsync(
                            frame.CorrelationId,
                            SpeechWorkerProtocol.DeserializeAudio(frame),
                            false)
                        .ConfigureAwait(false);
                    await ReplyBatchAsync(pipe, frame.CorrelationId, batch).ConfigureAwait(false);
                    break;
                }
                case SpeechWorkerMessageType.Finish:
                {
                    var batch = await runtime
                        .AcceptAudioAsync(frame.CorrelationId, [], true)
                        .ConfigureAwait(false);
                    await ReplyBatchAsync(pipe, frame.CorrelationId, batch).ConfigureAwait(false);
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

static async Task ReplyBatchAsync(
    Stream stream,
    long correlationId,
    WhisperBatch batch)
{
    foreach (var hypothesis in batch.Hypotheses)
    {
        await ReplyJsonAsync(
            stream,
            SpeechWorkerMessageType.Hypothesis,
            correlationId,
            hypothesis).ConfigureAwait(false);
    }

    await ReplyJsonAsync(
        stream,
        SpeechWorkerMessageType.Performance,
        correlationId,
        batch.Performance).ConfigureAwait(false);
    await ReadyAsync(stream, correlationId, batch.Stage, "complete").ConfigureAwait(false);
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

static Task ReadyAsync(
    Stream stream,
    long correlationId,
    string stage,
    string detail) =>
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

internal sealed class WhisperRuntime : IAsyncDisposable
{
    private const int SampleRate = 16_000;
    private readonly string _backend;
    private WhisperFactory? _factory;
    private VoiceActivityDetector? _vad;
    private VadAudioBuffer? _audioBuffer;
    private string _language = "auto";
    private int _threads = 2;
    private long _generation;
    private long _sequence;
    private double _audioMilliseconds;
    private bool _started;

    public WhisperRuntime(string backend)
    {
        _backend = backend;
    }

    public void Load(WorkerLoadRequest request)
    {
        Unload();
        if (!Path.IsPathFullyQualified(request.ModelPath) ||
            !File.Exists(request.ModelPath))
        {
            throw new FileNotFoundException(
                "The pinned Whisper model is missing.",
                request.ModelPath);
        }

        _factory = WhisperFactory.FromPath(request.ModelPath);
        _language = NormalizeLanguage(request.Language);
        _threads = Math.Clamp(request.Threads, 1, 16);
        var vadPath = request.Files is not null &&
                      request.Files.TryGetValue("vad", out var configuredVad)
            ? configuredVad
            : string.Empty;
        if (string.IsNullOrWhiteSpace(vadPath) || !File.Exists(vadPath))
        {
            throw new FileNotFoundException(
                "The pinned Silero VAD model is missing.",
                vadPath);
        }

        var policy = request.VadPolicy ?? new WorkerVadPolicy();
        _audioBuffer = new VadAudioBuffer(
            SampleRate,
            policy.PreRollMs,
            policy.PostRollMs,
            policy.MaximumSegmentMs);
        _vad = new VoiceActivityDetector(
            new VadModelConfig
            {
                SampleRate = SampleRate,
                NumThreads = _threads,
                Provider = "cpu",
                Debug = 0,
                SileroVad = new SileroVadModelConfig
                {
                    Model = vadPath,
                    Threshold = policy.Threshold,
                    MinSilenceDuration = policy.PostRollMs / 1000f,
                    MinSpeechDuration = 0.15f,
                    WindowSize = 512,
                    MaxSpeechDuration = policy.MaximumSegmentMs / 1000f,
                },
            },
            30f);
    }

    public async Task<WorkerPerformanceResponse> WarmupAsync()
    {
        EnsureLoaded();
        var stopwatch = Stopwatch.StartNew();
        _ = await TranscribeAsync(new float[SampleRate]).ConfigureAwait(false);
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
        _audioMilliseconds = 0;
        _audioBuffer!.Reset();
        _vad!.Reset();
        _started = true;
    }

    public async Task<WhisperBatch> AcceptAudioAsync(
        long sequence,
        float[] samples,
        bool finish)
    {
        EnsureStarted();
        _sequence = sequence;
        if (samples.Length > 0)
        {
            _audioBuffer!.Append(samples);
            _vad!.AcceptWaveform(samples);
            _audioMilliseconds += samples.Length * 1000d / SampleRate;
        }

        if (finish)
        {
            _vad!.Flush();
        }

        var hypotheses = new List<WorkerHypothesisResponse>();
        var decodeMilliseconds = 0d;
        var decodedAudioMilliseconds = 0d;
        var segmentCount = 0;
        while (!_vad!.IsEmpty())
        {
            var segment = _vad.Front();
            _vad.Pop();
            if (segment.Samples.Length == 0)
            {
                continue;
            }

            var contextualSamples = _audioBuffer!.Extract(
                segment.Start,
                segment.Samples.Length);
            if (contextualSamples.Length == 0)
            {
                contextualSamples = segment.Samples;
            }

            decodedAudioMilliseconds += contextualSamples.Length * 1000d / SampleRate;
            segmentCount++;
            var stopwatch = Stopwatch.StartNew();
            var text = await TranscribeAsync(contextualSamples).ConfigureAwait(false);
            stopwatch.Stop();
            decodeMilliseconds += stopwatch.Elapsed.TotalMilliseconds;
            if (!string.IsNullOrWhiteSpace(text))
            {
                hypotheses.Add(new WorkerHypothesisResponse(
                    _generation,
                    _sequence,
                    text,
                    _language,
                    true));
            }
        }

        if (finish)
        {
            _vad.Reset();
            _started = false;
        }

        var performance = new WorkerPerformanceResponse(
            _audioMilliseconds,
            decodeMilliseconds,
            Environment.WorkingSet,
            decodedAudioMilliseconds,
            segmentCount);
        _audioMilliseconds = 0;
        return new WhisperBatch(
            finish ? "finish" : "audio",
            hypotheses,
            performance);
    }

    public void Unload()
    {
        _started = false;
        _vad?.Dispose();
        _vad = null;
        _audioBuffer = null;
        _factory?.Dispose();
        _factory = null;
        _audioMilliseconds = 0;
    }

    public ValueTask DisposeAsync()
    {
        Unload();
        return ValueTask.CompletedTask;
    }

    private async Task<string> TranscribeAsync(float[] samples)
    {
        await using var processor = _factory!
            .CreateBuilder()
            .WithLanguage(_language)
            .WithThreads(_threads)
            .Build();
        var text = new System.Text.StringBuilder();
        await foreach (var segment in processor.ProcessAsync(samples))
        {
            text.Append(segment.Text);
        }

        return text.ToString().Trim();
    }

    private void EnsureLoaded()
    {
        if (_factory is null || _vad is null)
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

    private static string NormalizeLanguage(string value) =>
        string.IsNullOrWhiteSpace(value) ? "auto" : value.Trim().ToLowerInvariant();
}

internal sealed record WhisperBatch(
    string Stage,
    IReadOnlyList<WorkerHypothesisResponse> Hypotheses,
    WorkerPerformanceResponse Performance);
