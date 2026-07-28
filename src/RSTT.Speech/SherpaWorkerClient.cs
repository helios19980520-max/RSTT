using System.Diagnostics;
using System.IO.Pipes;
using RSTT.Core.Models;
using RSTT.Core.Workers;

namespace RSTT.Speech;

internal sealed class SherpaWorkerClient : IAsyncDisposable
{
    private readonly Process _process;
    private readonly NamedPipeClientStream _pipe;
    private long _correlationId;
    private bool _disposed;

    private SherpaWorkerClient(
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

    public static async Task<SherpaWorkerClient> StartAsync(
        ComputeBackend backend,
        CancellationToken cancellationToken,
        string? workerRoot = null)
    {
        if (backend is not ComputeBackend.Cpu and not ComputeBackend.Cuda)
        {
            throw new ArgumentOutOfRangeException(nameof(backend));
        }

        var executable = ResolveExecutable(backend, workerRoot);
        if (!File.Exists(executable))
        {
            throw new FileNotFoundException(
                $"{backend} sherpa worker is not installed at {executable}.",
                executable);
        }

        var pipeName = $"rstt-sherpa-{Environment.ProcessId}-{Guid.NewGuid():N}";
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
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
            const long correlation = 1;
            await SpeechWorkerProtocol.WriteAsync(
                pipe,
                SpeechWorkerProtocol.Json(
                    SpeechWorkerMessageType.Handshake,
                    correlation,
                    new WorkerHandshakeRequest(
                        typeof(SherpaWorkerClient).Assembly.GetName().Version?.ToString() ?? "unknown",
                        "sherpa-onnx")),
                timeout.Token).ConfigureAwait(false);
            var response = await ReadSingleAsync(
                pipe,
                correlation,
                SpeechWorkerMessageType.Ready,
                timeout.Token).ConfigureAwait(false);
            var handshake = SpeechWorkerProtocol.DeserializeJson<WorkerHandshakeResponse>(response);
            if (handshake.ProtocolVersion != SpeechWorkerProtocol.Version ||
                !handshake.Engine.Equals("sherpa-onnx", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Worker handshake returned protocol {handshake.ProtocolVersion}, engine '{handshake.Engine}'.");
            }

            var expectedBackend = backend == ComputeBackend.Cuda ? "cuda" : "cpu";
            if (!handshake.Backend.Equals(expectedBackend, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Worker backend '{handshake.Backend}' does not match requested backend '{expectedBackend}'.");
            }

            return new SherpaWorkerClient(process, pipe, backend, handshake)
            {
                _correlationId = correlation,
            };
        }
        catch
        {
            pipe.Dispose();
            Terminate(process);
            process.Dispose();
            throw;
        }
    }

    public async Task<WorkerBatchResult> LoadAsync(
        ModelInstallation installation,
        string language,
        CancellationToken cancellationToken)
    {
        var correlation = NextCorrelation();
        var policy = installation.Descriptor?.VadPolicy;
        var request = new WorkerLoadRequest(
            string.Empty,
            language,
            installation.NumThreads,
            false,
            installation.Engine,
            installation.FeatureDimension,
            installation.Files,
            policy is null
                ? null
                : new WorkerVadPolicy(
                    policy.PreRollMs,
                    policy.PostRollMs,
                    policy.MaximumSegmentMs,
                    policy.Threshold));
        await SpeechWorkerProtocol.WriteAsync(
            _pipe,
            SpeechWorkerProtocol.Json(SpeechWorkerMessageType.Load, correlation, request),
            cancellationToken).ConfigureAwait(false);
        return await ReadBatchAsync(correlation, cancellationToken).ConfigureAwait(false);
    }

    public async Task<WorkerBatchResult> WarmupAsync(CancellationToken cancellationToken)
    {
        var correlation = NextCorrelation();
        await SpeechWorkerProtocol.WriteAsync(
            _pipe,
            new SpeechWorkerFrame(
                SpeechWorkerMessageType.Warmup,
                correlation,
                ReadOnlyMemory<byte>.Empty),
            cancellationToken).ConfigureAwait(false);
        return await ReadBatchAsync(correlation, cancellationToken).ConfigureAwait(false);
    }

    public async Task StartSessionAsync(
        SessionGenerationId generation,
        long sequence,
        CancellationToken cancellationToken)
    {
        var correlation = NextCorrelation();
        await SpeechWorkerProtocol.WriteAsync(
            _pipe,
            SpeechWorkerProtocol.Json(
                SpeechWorkerMessageType.Start,
                correlation,
                new WorkerStartRequest(generation.Value, sequence)),
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
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await SpeechWorkerProtocol.WriteAsync(
                    _pipe,
                    new SpeechWorkerFrame(
                        SpeechWorkerMessageType.Shutdown,
                        correlation,
                        ReadOnlyMemory<byte>.Empty),
                    timeout.Token).ConfigureAwait(false);
                _ = await ReadBatchAsync(correlation, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (
            exception is IOException or OperationCanceledException or ObjectDisposedException)
        {
        }
        finally
        {
            _pipe.Dispose();
            if (!_process.WaitForExit(3_000))
            {
                Terminate(_process);
            }

            _process.Dispose();
        }
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
                ?? throw new EndOfStreamException("The sherpa worker disconnected.");
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
                    throw new SpeechWorkerException(
                        fault.Stage,
                        fault.Code,
                        fault.Message,
                        fault.IsRecoverable);
                }
                default:
                    throw new InvalidDataException(
                        $"Unexpected worker response {frame.Type}.");
            }
        }

        return new WorkerBatchResult(hypotheses, performance, ready);
    }

    private static async Task<SpeechWorkerFrame> ReadSingleAsync(
        Stream stream,
        long correlation,
        SpeechWorkerMessageType expected,
        CancellationToken cancellationToken)
    {
        var frame = await SpeechWorkerProtocol.ReadAsync(stream, cancellationToken).ConfigureAwait(false)
            ?? throw new EndOfStreamException("The sherpa worker disconnected.");
        if (frame.CorrelationId != correlation || frame.Type != expected)
        {
            throw new InvalidDataException(
                $"Expected {expected}/{correlation}, received {frame.Type}/{frame.CorrelationId}.");
        }

        return frame;
    }

    private static string ResolveExecutable(
        ComputeBackend backend,
        string? workerRoot)
    {
        var folder = backend == ComputeBackend.Cuda
            ? Path.Combine("workers", "sherpa-cuda12", "1.13.4")
            : Path.Combine("workers", "sherpa-cpu", "1.13.4");
        if (!string.IsNullOrWhiteSpace(workerRoot))
        {
            return Path.Combine(
                Path.GetFullPath(workerRoot),
                folder,
                "RSTT.Speech.Worker.exe");
        }

#if DEBUG
        const bool allowDevelopmentFallback = true;
#else
        const bool allowDevelopmentFallback = false;
#endif
        var project = backend == ComputeBackend.Cuda
            ? "RSTT.Sherpa.Cuda12.Worker"
            : "RSTT.Sherpa.Worker";
        return WorkerExecutableResolver.Resolve(
            AppContext.BaseDirectory,
            folder,
            "RSTT.Speech.Worker.exe",
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
                process.WaitForExit(3_000);
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

internal sealed record WorkerBatchResult(
    IReadOnlyList<WorkerHypothesisResponse> Hypotheses,
    WorkerPerformanceResponse? Performance,
    WorkerReadyResponse Ready);

internal sealed class SpeechWorkerException : Exception
{
    public SpeechWorkerException(
        string stage,
        string code,
        string message,
        bool isRecoverable)
        : base($"Speech worker failed during {stage} ({code}): {message}")
    {
        Stage = stage;
        Code = code;
        IsRecoverable = isRecoverable;
    }

    public string Stage { get; }

    public string Code { get; }

    public bool IsRecoverable { get; }
}
