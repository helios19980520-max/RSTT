using NAudio.Wave;

namespace RSTT.Audio;

/// <summary>
/// Converts capture callbacks to mono float samples and resamples them incrementally. Keeping
/// the final source sample between callbacks avoids gaps at callback boundaries.
/// </summary>
internal sealed class Pcm16kMonoConverter
{
    private const int TargetSampleRate = 16_000;
    private int _sourceSampleRate;
    private int _sourceChannels;
    private long _sourceFramesProcessed;
    private double _nextOutputSourcePosition;
    private float _lastSample;
    private bool _hasLastSample;

    public float[] Process(byte[] buffer, int byteCount, WaveFormat format)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(format);
        if (byteCount <= 0 || format.BlockAlign <= 0)
        {
            return [];
        }

        if (format.SampleRate != _sourceSampleRate || format.Channels != _sourceChannels)
        {
            Reset(format.SampleRate, format.Channels);
        }

        var frameCount = byteCount / format.BlockAlign;
        if (frameCount == 0)
        {
            return [];
        }

        var startSourceIndex = _sourceFramesProcessed - (_hasLastSample ? 1 : 0);
        var endSourceIndex = _sourceFramesProcessed + frameCount;
        var sourceStep = (double)format.SampleRate / TargetSampleRate;
        var remainingSourceFrames = (endSourceIndex - 1) - _nextOutputSourcePosition;
        var outputCount = remainingSourceFrames <= 0
            ? 0
            : (int)Math.Ceiling(remainingSourceFrames / sourceStep);
        var samples = new float[outputCount];
        var outputIndex = 0;
        while (outputIndex < samples.Length && _nextOutputSourcePosition < endSourceIndex - 1)
        {
            var localPosition = _nextOutputSourcePosition - startSourceIndex;
            var lowerIndex = (int)Math.Floor(localPosition);
            var sourceLength = frameCount + (_hasLastSample ? 1 : 0);
            if (lowerIndex < 0 || lowerIndex + 1 >= sourceLength)
            {
                break;
            }

            var fraction = (float)(localPosition - lowerIndex);
            var lower = ReadMonoFrame(buffer, lowerIndex, format);
            var upper = ReadMonoFrame(buffer, lowerIndex + 1, format);
            samples[outputIndex++] = lower + ((upper - lower) * fraction);
            _nextOutputSourcePosition += sourceStep;
        }

        if (outputIndex != samples.Length)
        {
            Array.Resize(ref samples, outputIndex);
        }

        _sourceFramesProcessed = endSourceIndex;
        _lastSample = ReadMonoFrame(buffer, frameCount - 1 + (_hasLastSample ? 1 : 0), format);
        _hasLastSample = true;
        return samples;
    }

    private void Reset(int sampleRate, int channels)
    {
        _sourceSampleRate = sampleRate;
        _sourceChannels = channels;
        _sourceFramesProcessed = 0;
        _nextOutputSourcePosition = 0;
        _hasLastSample = false;
    }

    private static float ReadSample(byte[] buffer, int offset, WaveFormat format)
    {
        if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
        {
            return BitConverter.ToSingle(buffer, offset);
        }

        return format.BitsPerSample switch
        {
            16 => BitConverter.ToInt16(buffer, offset) / 32768f,
            24 => Read24BitPcm(buffer, offset) / 8_388_608f,
            32 => BitConverter.ToInt32(buffer, offset) / 2_147_483_648f,
            _ => throw new NotSupportedException($"Unsupported capture format: {format.Encoding} {format.BitsPerSample}-bit."),
        };
    }

    private float ReadMonoFrame(byte[] buffer, int combinedFrameIndex, WaveFormat format)
    {
        if (_hasLastSample && combinedFrameIndex == 0)
        {
            return _lastSample;
        }

        var frameIndex = combinedFrameIndex - (_hasLastSample ? 1 : 0);
        var channelSum = 0f;
        var bytesPerSample = format.BitsPerSample / 8;
        for (var channel = 0; channel < format.Channels; channel++)
        {
            var sampleOffset = (frameIndex * format.BlockAlign) + (channel * bytesPerSample);
            channelSum += ReadSample(buffer, sampleOffset, format);
        }

        return Math.Clamp(channelSum / format.Channels, -1f, 1f);
    }

    private static int Read24BitPcm(byte[] buffer, int offset)
    {
        var value = buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16);
        return (value & 0x0080_0000) == 0 ? value : value | unchecked((int)0xff00_0000);
    }
}
