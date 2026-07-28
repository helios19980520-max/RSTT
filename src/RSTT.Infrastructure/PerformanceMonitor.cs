using System.Diagnostics;
using RSTT.Core.Abstractions;
using RSTT.Core.Models;

namespace RSTT.Infrastructure;

/// <summary>
/// Lock-light process and pipeline telemetry. Callers pull a snapshot at roughly 1 Hz;
/// no background polling thread is required.
/// </summary>
public sealed class PerformanceMonitor : IPerformanceMonitor, IDisposable
{
    private const int DecodeWindowSize = 256;
    private readonly Process _process = Process.GetCurrentProcess();
    private readonly object _decodeGate = new();
    private readonly double[] _decodeMilliseconds = new double[DecodeWindowSize];
    private readonly double[] _decodeAudioMilliseconds = new double[DecodeWindowSize];
    private long _audioCallbacks;
    private long _recognitionResults;
    private long _uiUpdates;
    private long _injections;
    private long _injectionAttempts;
    private long _injectionUtf16Units;
    private long _sendInputCalls;
    private long _injectionFailures;
    private int _injectionQueueHighWatermark;
    private long _coalescedUiEvents;
    private long _droppedAudioMilliseconds;
    private int _audioQueueDepth;
    private long _audioQueueDurationBits;
    private long _oldestAudioAgeBits;
    private int _decodeCount;
    private int _decodeIndex;
    private DateTimeOffset _lastSnapshotAt;
    private TimeSpan _lastCpuTime;
    private string _provider = "cpu";
    private string _modelId = string.Empty;
    private string _audioDevice = string.Empty;
    private bool _disposed;

    public PerformanceMonitor()
    {
        _lastSnapshotAt = DateTimeOffset.UtcNow;
        _lastCpuTime = _process.TotalProcessorTime;
    }

    public void SetSessionContext(string provider, string modelId, string audioDevice)
    {
        if (!string.IsNullOrWhiteSpace(provider))
        {
            Volatile.Write(ref _provider, provider);
        }

        if (!string.IsNullOrWhiteSpace(modelId))
        {
            Volatile.Write(ref _modelId, modelId);
        }

        if (!string.IsNullOrWhiteSpace(audioDevice))
        {
            Volatile.Write(ref _audioDevice, audioDevice);
        }
    }

    public void RecordAudioCallback(int sourceFrames, int sampleRate) =>
        Interlocked.Increment(ref _audioCallbacks);

    public void SetAudioQueue(int depth, double durationMs, double oldestAgeMs)
    {
        Volatile.Write(ref _audioQueueDepth, depth);
        Interlocked.Exchange(ref _audioQueueDurationBits, BitConverter.DoubleToInt64Bits(durationMs));
        Interlocked.Exchange(ref _oldestAudioAgeBits, BitConverter.DoubleToInt64Bits(oldestAgeMs));
    }

    public void RecordDroppedAudio(double durationMs) =>
        Interlocked.Add(ref _droppedAudioMilliseconds, Math.Max(0, (long)Math.Ceiling(durationMs)));

    public void RecordDecode(TimeSpan duration, double inputAudioDurationMs)
    {
        var milliseconds = Math.Max(0, duration.TotalMilliseconds);
        lock (_decodeGate)
        {
            _decodeMilliseconds[_decodeIndex] = milliseconds;
            _decodeAudioMilliseconds[_decodeIndex] = Math.Max(0, inputAudioDurationMs);
            _decodeIndex = (_decodeIndex + 1) % DecodeWindowSize;
            _decodeCount = Math.Min(_decodeCount + 1, DecodeWindowSize);
        }
    }

    public void RecordRecognitionResult() => Interlocked.Increment(ref _recognitionResults);

    public void RecordUiUpdate(int coalescedEvents = 0)
    {
        Interlocked.Increment(ref _uiUpdates);
        if (coalescedEvents > 0)
        {
            Interlocked.Add(ref _coalescedUiEvents, coalescedEvents);
        }
    }

    public void RecordInjection() => Interlocked.Increment(ref _injections);

    public void RecordInjectionAttempt(
        int utf16Length,
        int sendInputCallCount,
        int queueDepth,
        bool succeeded)
    {
        Interlocked.Increment(ref _injectionAttempts);
        Interlocked.Add(ref _injectionUtf16Units, Math.Max(0, utf16Length));
        Interlocked.Add(ref _sendInputCalls, Math.Max(0, sendInputCallCount));
        if (!succeeded)
        {
            Interlocked.Increment(ref _injectionFailures);
        }

        var boundedDepth = Math.Max(0, queueDepth);
        while (true)
        {
            var current = Volatile.Read(ref _injectionQueueHighWatermark);
            if (boundedDepth <= current ||
                Interlocked.CompareExchange(
                    ref _injectionQueueHighWatermark,
                    boundedDepth,
                    current) == current)
            {
                break;
            }
        }
    }

    public PerformanceSnapshot GetSnapshot()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var now = DateTimeOffset.UtcNow;
        var elapsedSeconds = Math.Max(0.001, (now - _lastSnapshotAt).TotalSeconds);
        _process.Refresh();
        var cpuTime = _process.TotalProcessorTime;
        var processCpu = Math.Clamp(
            (cpuTime - _lastCpuTime).TotalSeconds / elapsedSeconds / Environment.ProcessorCount * 100,
            0,
            100);
        _lastSnapshotAt = now;
        _lastCpuTime = cpuTime;

        var audioCallbacks = Interlocked.Exchange(ref _audioCallbacks, 0);
        var results = Interlocked.Exchange(ref _recognitionResults, 0);
        var uiUpdates = Interlocked.Exchange(ref _uiUpdates, 0);
        var injections = Interlocked.Exchange(ref _injections, 0);
        var injectionAttempts = Interlocked.Exchange(ref _injectionAttempts, 0);
        var injectionUtf16Units = Interlocked.Exchange(ref _injectionUtf16Units, 0);
        var sendInputCalls = Interlocked.Exchange(ref _sendInputCalls, 0);

        double p50;
        double p95;
        double maximum;
        double realtimeFactor;
        lock (_decodeGate)
        {
            if (_decodeCount == 0)
            {
                p50 = 0;
                p95 = 0;
                maximum = 0;
                realtimeFactor = 0;
            }
            else
            {
                var values = _decodeMilliseconds[.._decodeCount].ToArray();
                Array.Sort(values);
                p50 = values[(int)Math.Floor((values.Length - 1) * 0.50)];
                p95 = values[(int)Math.Floor((values.Length - 1) * 0.95)];
                maximum = values[^1];
                var audioMilliseconds = _decodeAudioMilliseconds[.._decodeCount].Sum();
                realtimeFactor = audioMilliseconds <= 0
                    ? 0
                    : values.Sum() / audioMilliseconds;
            }
        }

        return new PerformanceSnapshot(
            now,
            processCpu,
            _process.WorkingSet64,
            GC.GetTotalMemory(false),
            GC.CollectionCount(0),
            GC.CollectionCount(1),
            GC.CollectionCount(2),
            _process.Threads.Count,
            audioCallbacks / elapsedSeconds,
            Volatile.Read(ref _audioQueueDepth),
            BitConverter.Int64BitsToDouble(Interlocked.Read(ref _audioQueueDurationBits)),
            BitConverter.Int64BitsToDouble(Interlocked.Read(ref _oldestAudioAgeBits)),
            Interlocked.Read(ref _droppedAudioMilliseconds),
            p50,
            p95,
            maximum,
            realtimeFactor,
            results / elapsedSeconds,
            uiUpdates / elapsedSeconds,
            injections / elapsedSeconds,
            injectionAttempts == 0
                ? 0
                : injectionUtf16Units / (double)injectionAttempts,
            sendInputCalls / elapsedSeconds,
            Volatile.Read(ref _injectionQueueHighWatermark),
            Interlocked.Read(ref _injectionFailures),
            Interlocked.Read(ref _coalescedUiEvents),
            Volatile.Read(ref _provider),
            Volatile.Read(ref _modelId),
            Volatile.Read(ref _audioDevice));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _process.Dispose();
    }
}
