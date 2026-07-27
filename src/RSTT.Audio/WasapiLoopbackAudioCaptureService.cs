using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using RSTT.Core.Abstractions;
using RSTT.Core.Models;

namespace RSTT.Audio;

public sealed partial class WasapiLoopbackAudioCaptureService : IAudioCaptureService
{
    private readonly ILogger<WasapiLoopbackAudioCaptureService> _logger;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly Pcm16kMonoConverter _converter = new();
    private Channel<AudioChunk> _chunks = CreateChannel();
    private WasapiLoopbackCapture? _capture;
    private long _sequenceNumber;
    private bool _disposed;

    public WasapiLoopbackAudioCaptureService(ILogger<WasapiLoopbackAudioCaptureService> logger)
    {
        _logger = logger;
    }

    public bool IsCapturing => _capture is not null;

    public Task<IReadOnlyList<AudioDevice>> GetOutputDevicesAsync(CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<AudioDevice>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var enumerator = new MMDeviceEnumerator();
            var defaultId = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID;
            var devices = enumerator
                .EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                .Select(device => new AudioDevice(device.ID, device.FriendlyName, device.ID == defaultId))
                .ToArray();
            return devices;
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

            _chunks = CreateChannel();
            _sequenceNumber = 0;
            using var enumerator = new MMDeviceEnumerator();
            var device = string.IsNullOrWhiteSpace(deviceId)
                ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                : enumerator.GetDevice(deviceId);

            _capture = new WasapiLoopbackCapture(device);
            _capture.DataAvailable += OnDataAvailable;
            _capture.RecordingStopped += OnRecordingStopped;
            _capture.StartRecording();
            LogCaptureStarted(_logger, device.FriendlyName, device.ID, _capture.WaveFormat.ToString());
        }
        catch (Exception exception)
        {
            _chunks.Writer.TryComplete(exception);
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
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            StopCaptureUnsafe();
        }
        finally
        {
            _lifecycleLock.Release();
        }
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
        _lifecycleLock.Dispose();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs eventArgs)
    {
        try
        {
            var capture = _capture;
            if (capture is null)
            {
                return;
            }

            var samples = _converter.Process(eventArgs.Buffer, eventArgs.BytesRecorded, capture.WaveFormat);
            if (samples.Length > 0)
            {
                var chunk = new AudioChunk(Interlocked.Increment(ref _sequenceNumber), DateTimeOffset.UtcNow, samples);
                if (!_chunks.Writer.TryWrite(chunk))
                {
                    LogAudioChunkDropped(_logger);
                }
            }
        }
        catch (Exception exception)
        {
            LogConversionFailure(_logger, exception);
            _chunks.Writer.TryComplete(exception);
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs eventArgs)
    {
        if (eventArgs.Exception is not null)
        {
            LogCaptureStoppedUnexpectedly(_logger, eventArgs.Exception);
            _chunks.Writer.TryComplete(eventArgs.Exception);
            var capture = Interlocked.Exchange(ref _capture, null);
            if (capture is not null)
            {
                capture.DataAvailable -= OnDataAvailable;
                capture.RecordingStopped -= OnRecordingStopped;
                capture.Dispose();
            }
        }
    }

    private void StopCaptureUnsafe()
    {
        var capture = _capture;
        if (capture is null)
        {
            return;
        }

        _capture = null;
        capture.DataAvailable -= OnDataAvailable;
        capture.RecordingStopped -= OnRecordingStopped;
        capture.StopRecording();
        capture.Dispose();
        _chunks.Writer.TryComplete();
        LogCaptureStopped(_logger);
    }

    private static Channel<AudioChunk> CreateChannel() => Channel.CreateBounded<AudioChunk>(new BoundedChannelOptions(96)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = true,
    });

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    [LoggerMessage(LogLevel.Information, "Started WASAPI loopback capture from {DeviceName} ({DeviceId}) at {Format}.")]
    private static partial void LogCaptureStarted(ILogger logger, string deviceName, string deviceId, string format);

    [LoggerMessage(LogLevel.Error, "Unable to start WASAPI loopback capture.")]
    private static partial void LogCaptureStartFailure(ILogger logger, Exception exception);

    [LoggerMessage(LogLevel.Warning, "Audio processing is behind; a capture chunk was dropped to preserve live latency.")]
    private static partial void LogAudioChunkDropped(ILogger logger);

    [LoggerMessage(LogLevel.Error, "Failed to normalize a WASAPI capture chunk.")]
    private static partial void LogConversionFailure(ILogger logger, Exception exception);

    [LoggerMessage(LogLevel.Error, "WASAPI loopback capture stopped unexpectedly.")]
    private static partial void LogCaptureStoppedUnexpectedly(ILogger logger, Exception exception);

    [LoggerMessage(LogLevel.Information, "Stopped WASAPI loopback capture.")]
    private static partial void LogCaptureStopped(ILogger logger);
}
