using System.Diagnostics;
using System.IO.Pipes;
using RSTT.Core.Models;
using RSTT.Core.Workers;

namespace RSTT.Speech;

internal sealed class WhisperWorkerClient : IAsyncDisposable
{
    private readonly Process _process;
    private readonly NamedPipeClientStream _pipe;
    private long _correlationId;
    private bool _disposed;

    private WhisperWorkerClient(
        Process process,
        NamedPipeClientStream pipe,
        ComputeBackend backend,
        WorkerHandshakeResponse handshake)
    {
        _process = process;
        _pipe = pipe;
        Backend = backend;
        Handshake = handshake;
    }

    public ComputeBackend Backend { get; }

    public WorkerHandshakeResponse Handshake { get; }

    public static async Task<WhisperWorkerClient> StartAsync(
        ComputeBackend backend,
        CancellationToken cancellationToken,
        string? workerRoot = null)
    {
        var executable = ResolveExecutable(backend, workerRoot);
        if (!File.Exists(executable))
        {
            throw new FileNotFoundException(
                $"{backend} Whisper worker is not installed at {executable}.",
                executable);
        }

        var pipeName = $"rstt-whisper-{Environment.ProcessId}-{Guid.NewGuid():N}";
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(executable)
                ?? AppContext.BaseDirectory,
        };
        startInfo.ArgumentList.Add("--pipe");
        startInfo.ArgumentList.Add(pipeName);
        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start {executable}.");
        var pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough);
        try
        {
            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            await pipe.ConnectAsync(connectTimeout.Token).ConfigureAwait(false);
            var correlationId = 1L;
            await SpeechWorkerProtocol.WriteAsync(
                pipe,
                SpeechWorkerProtocol.Json(
                    SpeechWorkerMessageType.Handshake,
                    correlationId,
                    new WorkerHandshakeRequest(
                        typeof(WhisperWorkerClient).Assembly.GetName().Version?.ToString() ?? "unknown",
                        "whisper.cpp")),
                cancellationToken).ConfigureAwait(false);
            var response = await ReadExpectedAsync(
                pipe,
                correlationId,
                SpeechWorkerMessageType.Ready,
                cancellationToken).ConfigureAwait(false);
            var handshake = SpeechWorkerProtocol.DeserializeJson<WorkerHandshakeResponse>(response);
            if (handshake.ProtocolVersion != SpeechWorkerProtocol.Version ||
                !string.Equals(handshake.Engine, "whisper.cpp", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Worker handshake returned protocol {handshake.ProtocolVersion}, engine '{handshake.Engine}'.");
            }

            var expectedBackend = backend == ComputeBackend.Cuda ? "cuda" : "cpu";
            if (!handshake.Backend.Equals(
                    expectedBackend,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Worker backend '{handshake.Backend}' does not match requested backend '{expectedBackend}'.");
            }

            var client = new WhisperWorkerClient(process, pipe, backend, handshake)
            {
                _correlationId = correlationId,
            };
            return client;
        }
        catch
        {
            pipe.Dispose();
            Terminate(process);
            process.Dispose();
            throw;
        }
    }

    public async Task LoadAsync(
        string modelPath,
        string vadPath,
        string language,
        int threads,
        WorkerVadPolicy vadPolicy,
        CancellationToken cancellationToken)
    {
        var correlation = NextCorrelation();
        await SpeechWorkerProtocol.WriteAsync(
            _pipe,
            SpeechWorkerProtocol.Json(
                SpeechWorkerMessageType.Load,
                correlation,
                new WorkerLoadRequest(
                    modelPath,
                    language,
                    threads,
                    Files: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["vad"] = vadPath,
                    },
                    VadPolicy: vadPolicy)),
            cancellationToken).ConfigureAwait(false);
        _ = await ReadBatchAsync(correlation, cancellationToken).ConfigureAwait(false);
    }

    public async Task WarmupAsync(CancellationToken cancellationToken)
    {
        var correlation = NextCorrelation();
        await SpeechWorkerProtocol.WriteAsync(
            _pipe,
            new SpeechWorkerFrame(SpeechWorkerMessageType.Warmup, correlation, ReadOnlyMemory<byte>.Empty),
            cancellationToken).ConfigureAwait(false);
        _ = await ReadBatchAsync(correlation, cancellationToken).ConfigureAwait(false);
    }

    public async Task StartSessionAsync(
        long generation,
        long sequence,
        CancellationToken cancellationToken)
    {
        var correlation = NextCorrelation();
        await SpeechWorkerProtocol.WriteAsync(
            _pipe,
            SpeechWorkerProtocol.Json(
                SpeechWorkerMessageType.Start,
                correlation,
                new WorkerStartRequest(generation, sequence)),
            cancellationToken).ConfigureAwait(false);
        _ = await ReadBatchAsync(correlation, cancellationToken).ConfigureAwait(false);
    }

    public async Task<WorkerBatchResult> ProcessAudioAsync(
        AudioChunk chunk,
        CancellationToken cancellationToken)
    {
        await SpeechWorkerProtocol.WriteAsync(
            _pipe,
            SpeechWorkerProtocol.Audio(chunk.SequenceNumber, chunk.Samples),
            cancellationToken).ConfigureAwait(false);
        return await ReadBatchAsync(chunk.SequenceNumber, cancellationToken).ConfigureAwait(false);
    }

    public async Task<WorkerBatchResult> FinishAsync(CancellationToken cancellationToken)
    {
        var correlation = NextCorrelation();
        await SpeechWorkerProtocol.WriteAsync(
            _pipe,
            new SpeechWorkerFrame(
                SpeechWorkerMessageType.Finish,
                correlation,
                ReadOnlyMemory<byte>.Empty),
            cancellationToken).ConfigureAwait(false);
        return await ReadBatchAsync(correlation, cancellationToken).ConfigureAwait(false);
    }

    public async Task<(WorkerHypothesisResponse Hypothesis, WorkerPerformanceResponse Performance)> DecodeAsync(
        long generation,
        long sequence,
        float[] samples,
        CancellationToken cancellationToken)
    {
        await StartSessionAsync(generation, sequence, cancellationToken).ConfigureAwait(false);
        var audioHypotheses = new List<WorkerHypothesisResponse>();
        var audioMilliseconds = 0d;
        var decodeMilliseconds = 0d;
        var decodedAudioMilliseconds = 0d;
        var segmentCount = 0;
        var workingSet = 0L;
        const int vadFrameSamples = 1_600;
        for (var offset = 0; offset < samples.Length; offset += vadFrameSamples)
        {
            var length = Math.Min(vadFrameSamples, samples.Length - offset);
            var frameSamples = new float[length];
            Array.Copy(samples, offset, frameSamples, 0, length);
            var audio = await ProcessAudioAsync(
                new AudioChunk(
                    sequence,
                    DateTimeOffset.UtcNow,
                    frameSamples,
                    new SessionGenerationId(generation)),
                cancellationToken).ConfigureAwait(false);
            audioHypotheses.AddRange(audio.Hypotheses);
            if (audio.Performance is { } performance)
            {
                audioMilliseconds += performance.AudioMilliseconds;
                decodeMilliseconds += performance.DecodeMilliseconds;
                decodedAudioMilliseconds += performance.DecodedAudioMilliseconds;
                segmentCount += performance.SegmentCount;
                workingSet = Math.Max(workingSet, performance.WorkingSetBytes);
            }
        }

        var final = await FinishAsync(cancellationToken).ConfigureAwait(false);
        var hypothesis = final.Hypotheses.Count > 0
            ? final.Hypotheses[^1]
            : audioHypotheses.Count > 0
                ? audioHypotheses[^1]
                : new WorkerHypothesisResponse(
                    generation,
                    sequence,
                    string.Empty,
                    "auto",
                    true);
        var finalPerformance = final.Performance;
        return (
            hypothesis,
            new WorkerPerformanceResponse(
                audioMilliseconds +
                    (finalPerformance?.AudioMilliseconds ?? 0),
                decodeMilliseconds +
                    (finalPerformance?.DecodeMilliseconds ?? 0),
                Math.Max(
                    workingSet,
                    finalPerformance?.WorkingSetBytes ?? 0),
                decodedAudioMilliseconds +
                    (finalPerformance?.DecodedAudioMilliseconds ?? 0),
                segmentCount +
                    (finalPerformance?.SegmentCount ?? 0)));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            if (_pipe.IsConnected)
            {
                var correlation = NextCorrelation();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await SpeechWorkerProtocol.WriteAsync(
                    _pipe,
                    new SpeechWorkerFrame(
                        SpeechWorkerMessageType.Shutdown,
                        correlation,
                        ReadOnlyMemory<byte>.Empty),
                    timeout.Token).ConfigureAwait(false);
                _ = await ReadExpectedAsync(
                    _pipe,
                    correlation,
                    SpeechWorkerMessageType.Ready,
                    timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (
            exception is IOException or OperationCanceledException or ObjectDisposedException)
        {
        }
        finally
        {
            _pipe.Dispose();
            if (!_process.WaitForExit(2_000))
            {
                Terminate(_process);
            }

            _process.Dispose();
        }
    }

    private static async Task<SpeechWorkerFrame> ReadExpectedAsync(
        Stream stream,
        long correlationId,
        SpeechWorkerMessageType expected,
        CancellationToken cancellationToken)
    {
        var response = await SpeechWorkerProtocol.ReadAsync(stream, cancellationToken).ConfigureAwait(false)
            ?? throw new EndOfStreamException("The Whisper worker disconnected.");
        if (response.CorrelationId != correlationId)
        {
            throw new InvalidDataException(
                $"Worker correlation mismatch: expected {correlationId}, received {response.CorrelationId}.");
        }

        if (response.Type == SpeechWorkerMessageType.Fault)
        {
            var fault = SpeechWorkerProtocol.DeserializeJson<WorkerFaultResponse>(response);
            throw new InvalidOperationException(
                $"Whisper worker failed during {fault.Stage} ({fault.Code}): {fault.Message}");
        }

        if (response.Type != expected)
        {
            throw new InvalidDataException(
                $"Expected worker {expected}, received {response.Type}.");
        }

        return response;
    }

    private async Task<WorkerBatchResult> ReadBatchAsync(
        long correlation,
        CancellationToken cancellationToken)
    {
        var hypotheses = new List<WorkerHypothesisResponse>();
        WorkerPerformanceResponse? performance = null;
        WorkerReadyResponse? ready = null;
        while (ready is null)
        {
            var frame = await SpeechWorkerProtocol
                .ReadAsync(_pipe, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new EndOfStreamException("The Whisper worker disconnected.");
            if (frame.CorrelationId != correlation)
            {
                throw new InvalidDataException(
                    $"Worker correlation mismatch: expected {correlation}, received {frame.CorrelationId}.");
            }

            switch (frame.Type)
            {
                case SpeechWorkerMessageType.Hypothesis:
                    hypotheses.Add(
                        SpeechWorkerProtocol.DeserializeJson<WorkerHypothesisResponse>(frame));
                    break;
                case SpeechWorkerMessageType.Performance:
                    performance =
                        SpeechWorkerProtocol.DeserializeJson<WorkerPerformanceResponse>(frame);
                    break;
                case SpeechWorkerMessageType.Ready:
                    ready = SpeechWorkerProtocol.DeserializeJson<WorkerReadyResponse>(frame);
                    break;
                case SpeechWorkerMessageType.Fault:
                {
                    var fault = SpeechWorkerProtocol.DeserializeJson<WorkerFaultResponse>(frame);
                    throw new InvalidOperationException(
                        $"Whisper worker failed during {fault.Stage} ({fault.Code}): {fault.Message}");
                }
                default:
                    throw new InvalidDataException(
                        $"Unexpected Whisper worker response {frame.Type}.");
            }
        }

        return new WorkerBatchResult(hypotheses, performance, ready);
    }

    private static string ResolveExecutable(
        ComputeBackend backend,
        string? workerRoot)
    {
        var workerName = backend == ComputeBackend.Cuda
            ? "RSTT.Whisper.Cuda12.Worker.exe"
            : "RSTT.Whisper.Worker.exe";
        var folder = backend == ComputeBackend.Cuda
            ? Path.Combine("workers", "whisper-cuda12", "1.9.1")
            : Path.Combine("workers", "whisper-cpu", "1.9.1");
        if (!string.IsNullOrWhiteSpace(workerRoot))
        {
            return Path.Combine(
                Path.GetFullPath(workerRoot),
                folder,
                workerName);
        }

#if DEBUG
        const bool allowDevelopmentFallback = true;
#else
        const bool allowDevelopmentFallback = false;
#endif
        var project = backend == ComputeBackend.Cuda
            ? "RSTT.Whisper.Cuda12.Worker"
            : "RSTT.Whisper.Worker";
        return WorkerExecutableResolver.Resolve(
            AppContext.BaseDirectory,
            folder,
            workerName,
            project,
            allowDevelopmentFallback);
    }

    private long NextCorrelation() => Interlocked.Increment(ref _correlationId);

    private static void Terminate(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(true);
                process.WaitForExit(2_000);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }
}
