namespace RSTT.Core.Workers;

/// <summary>
/// Bounded session-relative PCM history used to restore pre/post-roll around
/// native VAD segments without retaining an entire long-running session.
/// </summary>
public sealed class VadAudioBuffer
{
    private readonly int _preRollSamples;
    private readonly int _postRollSamples;
    private readonly int _retainedSamples;
    private readonly List<float> _samples;
    private long _firstSampleIndex;
    private long _totalSamples;

    public VadAudioBuffer(
        int sampleRate,
        int preRollMs,
        int postRollMs,
        int maximumSegmentMs)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentOutOfRangeException.ThrowIfNegative(preRollMs);
        ArgumentOutOfRangeException.ThrowIfNegative(postRollMs);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumSegmentMs);
        _preRollSamples = MillisecondsToSamples(sampleRate, preRollMs);
        _postRollSamples = MillisecondsToSamples(sampleRate, postRollMs);
        _retainedSamples =
            MillisecondsToSamples(
                sampleRate,
                maximumSegmentMs + preRollMs + postRollMs) +
            sampleRate;
        _samples = new List<float>(_retainedSamples);
    }

    public int Count => _samples.Count;

    public long TotalSamples => _totalSamples;

    public void Append(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty)
        {
            return;
        }

        _samples.AddRange(samples.ToArray());
        _totalSamples += samples.Length;
        var overflow = _samples.Count - _retainedSamples;
        if (overflow > 0)
        {
            _samples.RemoveRange(0, overflow);
            _firstSampleIndex += overflow;
        }
    }

    public float[] Extract(long segmentStart, int segmentLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(segmentStart);
        ArgumentOutOfRangeException.ThrowIfNegative(segmentLength);
        var requestedStart = Math.Max(0, segmentStart - _preRollSamples);
        var requestedEnd = Math.Min(
            _totalSamples,
            segmentStart + segmentLength + _postRollSamples);
        var availableStart = Math.Max(requestedStart, _firstSampleIndex);
        var availableEnd = Math.Min(
            requestedEnd,
            _firstSampleIndex + _samples.Count);
        if (availableEnd <= availableStart)
        {
            return [];
        }

        var offset = checked((int)(availableStart - _firstSampleIndex));
        var count = checked((int)(availableEnd - availableStart));
        return _samples.GetRange(offset, count).ToArray();
    }

    public void Reset()
    {
        _samples.Clear();
        _firstSampleIndex = 0;
        _totalSamples = 0;
    }

    private static int MillisecondsToSamples(int sampleRate, int milliseconds) =>
        checked((int)Math.Ceiling(sampleRate * milliseconds / 1000d));
}
