using RSTT.Core.Models;

namespace RSTT.Core.Abstractions;

public interface IPerformanceMonitor
{
    void SetSessionContext(string provider, string modelId, string audioDevice);

    void RecordAudioCallback(int sourceFrames, int sampleRate);

    void SetAudioQueue(int depth, double durationMs, double oldestAgeMs);

    void RecordDroppedAudio(double durationMs);

    void RecordDecode(TimeSpan duration, double inputAudioDurationMs);

    void RecordRecognitionResult();

    void RecordUiUpdate(int coalescedEvents = 0);

    void RecordInjection();

    void RecordInjectionAttempt(
        int utf16Length,
        int sendInputCallCount,
        int queueDepth,
        bool succeeded);

    PerformanceSnapshot GetSnapshot();
}

public sealed class NullPerformanceMonitor : IPerformanceMonitor
{
    public static NullPerformanceMonitor Instance { get; } = new();

    private NullPerformanceMonitor()
    {
    }

    public void SetSessionContext(string provider, string modelId, string audioDevice)
    {
    }

    public void RecordAudioCallback(int sourceFrames, int sampleRate)
    {
    }

    public void SetAudioQueue(int depth, double durationMs, double oldestAgeMs)
    {
    }

    public void RecordDroppedAudio(double durationMs)
    {
    }

    public void RecordDecode(TimeSpan duration, double inputAudioDurationMs)
    {
    }

    public void RecordRecognitionResult()
    {
    }

    public void RecordUiUpdate(int coalescedEvents = 0)
    {
    }

    public void RecordInjection()
    {
    }

    public void RecordInjectionAttempt(
        int utf16Length,
        int sendInputCallCount,
        int queueDepth,
        bool succeeded)
    {
    }

    public PerformanceSnapshot GetSnapshot() => new(
        DateTimeOffset.UtcNow,
        0,
        0,
        GC.GetTotalMemory(false),
        GC.CollectionCount(0),
        GC.CollectionCount(1),
        GC.CollectionCount(2),
        Environment.ProcessorCount,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        string.Empty,
        string.Empty,
        string.Empty);
}
