using System.Buffers;
using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using RSTT.Core.Abstractions;
using RSTT.Core.Models;

namespace RSTT.Audio;

/// <summary>
/// The WASAPI callback only copies the native packet into a pooled buffer and returns.
/// Downmixing, resampling, metering, and managed allocation happen on one input-driven worker.
/// </summary>
public sealed partial class WasapiLoopbackAudioCaptureService : IAudioCaptureService
{
    private const int RawQueueCapacity = 32;
    private const int NormalizedQueueCapacity = 48;
    private static readonly TimeSpan MeterInterval = TimeSpan.FromMilliseconds(40);
    private static readonly TimeSpan RepeatedWarningInterval = TimeSpan.FromSeconds(5);

    private readonly ILogger<WasapiLoopbackAudioCaptureService> _logger;
    private readonly IPerformanceMonitor _performance;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly Pcm16kMonoConverter _converter = new();
    private Channel<RawAudioPacket> _rawPackets = CreateRawChannel();
    private Channel<AudioChunk> _chunks = CreateNormalizedChannel();
    private WasapiLoopbackCapture? _capture;
    private CancellationTokenSource? _processingCancellation;
    private Task? _processingWorker;
    private long _sequenceNumber;
    private long _queuedSourceFrames;
    private long _oldestQueuedAtTicks;
    private int _rawQueueDepth;
    private long _lastDropWarningTicks;
    private double _smoothedLevel;
    private bool _disposed;

    public WasapiLoopbackAudioCaptureService(
        ILogger<WasapiLoopbackAudioCaptureService> logger,
        IPerformanceMonitor? performance = null)
    {
        _logger = logger;
        _performance = performance ?? NullPerformanceMonitor.Instance;
    }

    public bool IsCapturing => _capture is not null;

    public event EventHandler<AudioLevelEventArgs>? AudioLevelChanged;

    public Task<IReadOnlyList<AudioDevice>> GetOutputDevicesAsync(CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<AudioDevice>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var enumerator = new MMDeviceEnumerator();
            var defaultId = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID;
            return enumerator
                .EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                .Select(device => new AudioDevice(device.ID, device.FriendlyName, device.ID == defaultId))
                .ToArray();
        }, cancellationToken);

    public async Task StartAsync(string? deviceId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_capture is not null)
            {
                return;
            }

            _rawPackets = CreateRawChannel();
            _chunks = CreateNormalizedChannel();
            _sequenceNumber = 0;
            _smoothedLevel = 0;
            _rawQueueDepth = 0;
            _queuedSourceFrames = 0;
            _oldestQueuedAtTicks = 0;
            _processingCancellation?.Dispose();
            _processingCancellation = new CancellationTokenSource();
            _processingWorker = Task.Run(
                () => ProcessRawAudioAsync(_processingCancellation.Token),
                CancellationToken.None);

            using var enumerator = new MMDeviceEnumerator();
            var device = string.IsNullOrWhiteSpace(deviceId)
                ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                : enumerator.GetDevice(deviceId);
            _capture = new WasapiLoopbackCapture(device);
            _capture.DataAvailable += OnDataAvailable;
            _capture.RecordingStopped += OnRecordingStopped;
            _performance.SetSessionContext(string.Empty, string.Empty, device.FriendlyName);
            _capture.StartRecording();
            LogCaptureStarted(_logger, device.FriendlyName, device.ID, _capture.WaveFormat.ToString());
        }
        catch (Exception exception)
        {
            _rawPackets.Writer.TryComplete(exception);
            _chunks.Writer.TryComplete(exception);
            _processingCancellation?.Cancel();
            LogCaptureStartFailure(_logger, exception);
            throw;
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        Task? processingWorker;
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            StopCaptureUnsafe();
            processingWorker = _processingWorker;
        }
        finally
        {
            _lifecycleLock.Release();
        }

        if (processingWorker is not null)
        {
            try
            {
                await processingWorker.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _processingCancellation?.Cancel();
                throw;
            }
        }

        _processingWorker = null;
        _processingCancellation?.Dispose();
        _processingCancellation = null;
    }

    public IAsyncEnumerable<AudioChunk> ReadChunksAsync(CancellationToken cancellationToken = default) =>
        _chunks.Reader.ReadAllAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await StopAsync().ConfigureAwait(false);
        _processingCancellation?.Cancel();
        _processingCancellation?.Dispose();
        _lifecycleLock.Dispose();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs eventArgs)
    {
        var capture = _capture;
        if (capture is null || eventArgs.BytesRecorded <= 0)
        {
            return;
        }

        var format = capture.WaveFormat;
        var frameCount = eventArgs.BytesRecorded / Math.Max(1, format.BlockAlign);
        _performance.RecordAudioCallback(frameCount, format.SampleRate);
        var buffer = ArrayPool<byte>.Shared.Rent(eventArgs.BytesRecorded);
        Buffer.BlockCopy(eventArgs.Buffer, 0, buffer, 0, eventArgs.BytesRecorded);
        var packet = new RawAudioPacket(
            buffer,
            eventArgs.BytesRecorded,
            frameCount,
            format,
            DateTimeOffset.UtcNow);

        if (!_rawPackets.Writer.TryWrite(packet))
        {
            ArrayPool<byte>.Shared.Return(buffer);
            _performance.RecordDroppedAudio(frameCount * 1000d / format.SampleRate);
            LogDroppedAudioRateLimited();
            return;
        }

        var depth = Interlocked.Increment(ref _rawQueueDepth);
        Interlocked.Add(ref _queuedSourceFrames, frameCount);
        if (depth == 1)
        {
            Interlocked.Exchange(ref _oldestQueuedAtTicks, packet.CapturedAt.UtcTicks);
        }

        UpdateQueueMetrics(format.SampleRate);
    }

    private async Task ProcessRawAudioAsync(CancellationToken cancellationToken)
    {
        var meterStopwatch = Stopwatch.StartNew();
        try
        {
            await foreach (var packet in _rawPackets.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    Interlocked.Decrement(ref _rawQueueDepth);
                    Interlocked.Add(ref _queuedSourceFrames, -packet.SourceFrames);
                    if (Volatile.Read(ref _rawQueueDepth) == 0)
                    {
                        Interlocked.Exchange(ref _oldestQueuedAtTicks, 0);
                    }

                    UpdateQueueMetrics(packet.Format.SampleRate);
                    var samples = _converter.Process(packet.Buffer, packet.ByteCount, packet.Format);
                    if (samples.Length == 0)
                    {
                        continue;
                    }

                    var (level, peak) = MeasureAudio(samples);
                    _smoothedLevel = (_smoothedLevel * 0.72) + (level * 0.28);
                    if (meterStopwatch.Elapsed >= MeterInterval)
                    {
                        meterStopwatch.Restart();
                        AudioLevelChanged?.Invoke(
                            this,
                            new AudioLevelEventArgs(_smoothedLevel, peak));
                    }

                    var chunk = new AudioChunk(
                        Interlocked.Increment(ref _sequenceNumber),
                        packet.CapturedAt,
                        samples);
                    if (!_chunks.Writer.TryWrite(chunk))
                    {
                        _performance.RecordDroppedAudio(chunk.Duration.TotalMilliseconds);
                        LogDroppedAudioRateLimited();
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(packet.Buffer);
                }
            }

            _chunks.Writer.TryComplete();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _chunks.Writer.TryComplete();
        }
        catch (Exception exception)
        {
            LogConversionFailure(_logger, exception);
            _chunks.Writer.TryComplete(exception);
        }
        finally
        {
            while (_rawPackets.Reader.TryRead(out var packet))
            {
                ArrayPool<byte>.Shared.Return(packet.Buffer);
            }
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs eventArgs)
    {
        if (eventArgs.Exception is null)
        {
            return;
        }

        LogCaptureStoppedUnexpectedly(_logger, eventArgs.Exception);
        _rawPackets.Writer.TryComplete(eventArgs.Exception);
        var capture = Interlocked.Exchange(ref _capture, null);
        if (capture is not null)
        {
            capture.DataAvailable -= OnDataAvailable;
            capture.RecordingStopped -= OnRecordingStopped;
            capture.Dispose();
        }
    }

    private void StopCaptureUnsafe()
    {
        var capture = Interlocked.Exchange(ref _capture, null);
        if (capture is null)
        {
            _rawPackets.Writer.TryComplete();
            return;
        }

        capture.DataAvailable -= OnDataAvailable;
        capture.RecordingStopped -= OnRecordingStopped;
        capture.StopRecording();
        capture.Dispose();
        _rawPackets.Writer.TryComplete();
        _smoothedLevel = 0;
        AudioLevelChanged?.Invoke(this, new AudioLevelEventArgs(0, 0));
        LogCaptureStopped(_logger);
    }

    private void UpdateQueueMetrics(int sampleRate)
    {
        var queuedFrames = Math.Max(0, Interlocked.Read(ref _queuedSourceFrames));
        var oldestTicks = Interlocked.Read(ref _oldestQueuedAtTicks);
        var oldestAge = oldestTicks <= 0
            ? 0
            : Math.Max(0, (DateTimeOffset.UtcNow - new DateTimeOffset(oldestTicks, TimeSpan.Zero)).TotalMilliseconds);
        _performance.SetAudioQueue(
            Math.Max(0, Volatile.Read(ref _rawQueueDepth)),
            queuedFrames * 1000d / Math.Max(1, sampleRate),
            oldestAge);
    }

    private void LogDroppedAudioRateLimited()
    {
        var now = Stopwatch.GetTimestamp();
        var previous = Interlocked.Read(ref _lastDropWarningTicks);
        if (previous != 0 &&
            Stopwatch.GetElapsedTime(previous, now) < RepeatedWarningInterval)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _lastDropWarningTicks, now, previous) == previous)
        {
            LogAudioChunkDropped(_logger);
        }
    }

    private static (double Level, double Peak) MeasureAudio(float[] samples)
    {
        double sum = 0;
        double peak = 0;
        foreach (var sample in samples)
        {
            var absolute = Math.Abs(sample);
            peak = Math.Max(peak, absolute);
            sum += sample * sample;
        }

        var rms = Math.Sqrt(sum / samples.Length);
        var normalized = Math.Clamp((20 * Math.Log10(Math.Max(rms, 0.000_001)) + 60) / 60, 0, 1);
        return (normalized, Math.Clamp(peak, 0, 1));
    }

    private static Channel<RawAudioPacket> CreateRawChannel() =>
        Channel.CreateBounded<RawAudioPacket>(new BoundedChannelOptions(RawQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false,
        });

    private static Channel<AudioChunk> CreateNormalizedChannel() =>
        Channel.CreateBounded<AudioChunk>(new BoundedChannelOptions(NormalizedQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false,
        });

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed record RawAudioPacket(
        byte[] Buffer,
        int ByteCount,
        int SourceFrames,
        WaveFormat Format,
        DateTimeOffset CapturedAt);

    [LoggerMessage(LogLevel.Information, "Started WASAPI loopback capture from {DeviceName} ({DeviceId}) at {Format}.")]
    private static partial void LogCaptureStarted(
        ILogger logger,
        string deviceName,
        string deviceId,
        string format);

    [LoggerMessage(LogLevel.Error, "Unable to start WASAPI loopback capture.")]
    private static partial void LogCaptureStartFailure(ILogger logger, Exception exception);

    [LoggerMessage(LogLevel.Warning, "Audio processing is behind; a capture block was dropped to preserve live latency.")]
    private static partial void LogAudioChunkDropped(ILogger logger);

    [LoggerMessage(LogLevel.Error, "Failed to normalize a WASAPI capture block.")]
    private static partial void LogConversionFailure(ILogger logger, Exception exception);

    [LoggerMessage(LogLevel.Error, "WASAPI loopback capture stopped unexpectedly.")]
    private static partial void LogCaptureStoppedUnexpectedly(
        ILogger logger,
        Exception exception);

    [LoggerMessage(LogLevel.Information, "Stopped WASAPI loopback capture.")]
    private static partial void LogCaptureStopped(ILogger logger);
}
