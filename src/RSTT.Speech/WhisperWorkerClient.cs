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
        CancellationToken cancellationToken)
    {
        var executable = ResolveExecutable(backend);
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
        string language,
        int threads,
        CancellationToken cancellationToken)
    {
        var correlation = NextCorrelation();
        await SpeechWorkerProtocol.WriteAsync(
            _pipe,
            SpeechWorkerProtocol.Json(
                SpeechWorkerMessageType.Load,
                correlation,
                new WorkerLoadRequest(modelPath, language, threads)),
            cancellationToken).ConfigureAwait(false);
        _ = await ReadExpectedAsync(
            _pipe,
            correlation,
            SpeechWorkerMessageType.Ready,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task WarmupAsync(CancellationToken cancellationToken)
    {
        var correlation = NextCorrelation();
        await SpeechWorkerProtocol.WriteAsync(
            _pipe,
            new SpeechWorkerFrame(SpeechWorkerMessageType.Warmup, correlation, ReadOnlyMemory<byte>.Empty),
            cancellationToken).ConfigureAwait(false);
        _ = await ReadExpectedAsync(
            _pipe,
            correlation,
            SpeechWorkerMessageType.Ready,
            cancellationToken).ConfigureAwait(false);

        _ = await DecodeAsync(0, 0, new float[AudioChunk.SampleRate], cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<(WorkerHypothesisResponse Hypothesis, WorkerPerformanceResponse Performance)> DecodeAsync(
        long generation,
        long sequence,
        float[] samples,
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
        _ = await ReadExpectedAsync(
            _pipe,
            correlation,
            SpeechWorkerMessageType.Ready,
            cancellationToken).ConfigureAwait(false);

        await SpeechWorkerProtocol.WriteAsync(
            _pipe,
            SpeechWorkerProtocol.Audio(correlation, samples),
            cancellationToken).ConfigureAwait(false);
        _ = await ReadExpectedAsync(
            _pipe,
            correlation,
            SpeechWorkerMessageType.Ready,
            cancellationToken).ConfigureAwait(false);

        await SpeechWorkerProtocol.WriteAsync(
            _pipe,
            new SpeechWorkerFrame(SpeechWorkerMessageType.Finish, correlation, ReadOnlyMemory<byte>.Empty),
            cancellationToken).ConfigureAwait(false);
        var hypothesisFrame = await ReadExpectedAsync(
            _pipe,
            correlation,
            SpeechWorkerMessageType.Hypothesis,
            cancellationToken).ConfigureAwait(false);
        var performanceFrame = await ReadExpectedAsync(
            _pipe,
            correlation,
            SpeechWorkerMessageType.Performance,
            cancellationToken).ConfigureAwait(false);
        return (
            SpeechWorkerProtocol.DeserializeJson<WorkerHypothesisResponse>(hypothesisFrame),
            SpeechWorkerProtocol.DeserializeJson<WorkerPerformanceResponse>(performanceFrame));
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

    private static string ResolveExecutable(ComputeBackend backend)
    {
        var workerName = backend == ComputeBackend.Cuda
            ? "RSTT.Whisper.Cuda12.Worker.exe"
            : "RSTT.Whisper.Worker.exe";
        var folder = backend == ComputeBackend.Cuda
            ? Path.Combine("workers", "whisper-cuda12", "1.9.1")
            : Path.Combine("workers", "whisper-cpu", "1.9.1");
        var local = Path.Combine(AppContext.BaseDirectory, folder, workerName);
        if (File.Exists(local))
        {
            return local;
        }

        var repository = FindRepositoryRoot(AppContext.BaseDirectory);
        if (repository is null)
        {
            return local;
        }

        var project = backend == ComputeBackend.Cuda
            ? "RSTT.Whisper.Cuda12.Worker"
            : "RSTT.Whisper.Worker";
        var configuration = IsDebugBuild() ? "Debug" : "Release";
        return Path.Combine(
            repository,
            "src",
            project,
            "bin",
            configuration,
            "net8.0-windows",
            "win-x64",
            workerName);
    }

    private static string? FindRepositoryRoot(string start)
    {
        var directory = new DirectoryInfo(start);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "RSTT.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static bool IsDebugBuild()
    {
#if DEBUG
        return true;
#else
        return false;
#endif
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
