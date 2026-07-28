using System.Diagnostics;
using System.IO.Pipes;
using RSTT.Core.Workers;
using Whisper.net;
#if WHISPER_CUDA12
using Whisper.net.LibraryLoader;
#endif

const int AudioSampleRate = 16_000;
return await RunAsync(args).ConfigureAwait(false);

static async Task<int> RunAsync(string[] arguments)
{
    var pipeName = GetArgument(arguments, "--pipe");
    if (string.IsNullOrWhiteSpace(pipeName))
    {
        Console.Error.WriteLine("Usage: RSTT.Whisper.Worker --pipe <name>");
        return 2;
    }

#if WHISPER_CUDA12
    RuntimeOptions.RuntimeLibraryOrder = [RuntimeLibrary.Cuda12];
    const string backend = "cuda12";
#else
    const string backend = "cpu";
#endif

    await using var pipe = new NamedPipeServerStream(
        pipeName,
        PipeDirection.InOut,
        1,
        PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | PipeOptions.WriteThrough);
    await pipe.WaitForConnectionAsync().ConfigureAwait(false);

    WhisperFactory? factory = null;
    string language = "auto";
    int threads = Math.Clamp(Environment.ProcessorCount / 2, 1, 8);
    long generation = 0;
    long sequence = 0;
    var audio = new List<float>(AudioSampleRate * 20);
    try
    {
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
                                backend)).ConfigureAwait(false);
                        break;
                    case SpeechWorkerMessageType.Load:
                    {
                        var request = SpeechWorkerProtocol.DeserializeJson<WorkerLoadRequest>(frame);
                        if (!Path.IsPathFullyQualified(request.ModelPath) || !File.Exists(request.ModelPath))
                        {
                            throw new FileNotFoundException("The pinned Whisper model is missing.", request.ModelPath);
                        }

                        factory?.Dispose();
                        factory = WhisperFactory.FromPath(request.ModelPath);
                        language = NormalizeLanguage(request.Language);
                        threads = Math.Clamp(request.Threads, 1, 16);
                        await ReadyAsync(pipe, frame.CorrelationId, "load", backend).ConfigureAwait(false);
                        break;
                    }
                    case SpeechWorkerMessageType.Warmup:
                        EnsureLoaded(factory);
                        await ReadyAsync(pipe, frame.CorrelationId, "warmup", "model loaded").ConfigureAwait(false);
                        break;
                    case SpeechWorkerMessageType.Start:
                    {
                        EnsureLoaded(factory);
                        var request = SpeechWorkerProtocol.DeserializeJson<WorkerStartRequest>(frame);
                        generation = request.SessionGenerationId;
                        sequence = request.SequenceId;
                        audio.Clear();
                        await ReadyAsync(pipe, frame.CorrelationId, "start", "accepting audio").ConfigureAwait(false);
                        break;
                    }
                    case SpeechWorkerMessageType.Audio:
                        audio.AddRange(SpeechWorkerProtocol.DeserializeAudio(frame));
                        await ReadyAsync(
                            pipe,
                            frame.CorrelationId,
                            "audio",
                            audio.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)).ConfigureAwait(false);
                        break;
                    case SpeechWorkerMessageType.Finish:
                    {
                        EnsureLoaded(factory);
                        var stopwatch = Stopwatch.StartNew();
                        var resultText = await TranscribeAsync(
                            factory!,
                            audio.ToArray(),
                            language,
                            threads).ConfigureAwait(false);
                        stopwatch.Stop();
                        await ReplyJsonAsync(
                            pipe,
                            SpeechWorkerMessageType.Hypothesis,
                            frame.CorrelationId,
                            new WorkerHypothesisResponse(
                                generation,
                                sequence,
                                resultText,
                                language,
                                true)).ConfigureAwait(false);
                        await ReplyJsonAsync(
                            pipe,
                            SpeechWorkerMessageType.Performance,
                            frame.CorrelationId,
                            new WorkerPerformanceResponse(
                                audio.Count * 1000d / AudioSampleRate,
                                stopwatch.Elapsed.TotalMilliseconds,
                                Environment.WorkingSet)).ConfigureAwait(false);
                        audio.Clear();
                        break;
                    }
                    case SpeechWorkerMessageType.Unload:
                        factory?.Dispose();
                        factory = null;
                        audio.Clear();
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
    }
    finally
    {
        factory?.Dispose();
    }

    return 0;
}

static async Task<string> TranscribeAsync(
    WhisperFactory factory,
    float[] samples,
    string language,
    int threads)
{
    if (samples.Length == 0)
    {
        return string.Empty;
    }

    await using var processor = factory
        .CreateBuilder()
        .WithLanguage(language)
        .WithThreads(threads)
        .Build();
    var text = new System.Text.StringBuilder();
    await foreach (var segment in processor.ProcessAsync(samples))
    {
        text.Append(segment.Text);
    }

    return text.ToString().Trim();
}

static void EnsureLoaded(WhisperFactory? factory)
{
    if (factory is null)
    {
        throw new InvalidOperationException("Load a model before starting recognition.");
    }
}

static string NormalizeLanguage(string value) =>
    string.IsNullOrWhiteSpace(value) ? "auto" : value.Trim().ToLowerInvariant();

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
