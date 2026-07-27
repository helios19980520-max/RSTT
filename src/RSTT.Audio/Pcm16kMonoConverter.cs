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

        var mono = new float[frameCount];
        for (var frame = 0; frame < frameCount; frame++)
        {
            var channelSum = 0f;
            for (var channel = 0; channel < format.Channels; channel++)
            {
                var sampleOffset = (frame * format.BlockAlign) + (channel * (format.BitsPerSample / 8));
                channelSum += ReadSample(buffer, sampleOffset, format);
            }

            mono[frame] = Math.Clamp(channelSum / format.Channels, -1f, 1f);
        }

        var startSourceIndex = _sourceFramesProcessed - (_hasLastSample ? 1 : 0);
        var source = new float[mono.Length + (_hasLastSample ? 1 : 0)];
        if (_hasLastSample)
        {
            source[0] = _lastSample;
            Array.Copy(mono, 0, source, 1, mono.Length);
        }
        else
        {
            Array.Copy(mono, source, mono.Length);
        }

        var endSourceIndex = _sourceFramesProcessed + frameCount;
        var samples = new List<float>((int)Math.Ceiling(frameCount * (double)TargetSampleRate / format.SampleRate));
        while (_nextOutputSourcePosition < endSourceIndex - 1)
        {
            var localPosition = _nextOutputSourcePosition - startSourceIndex;
            var lowerIndex = (int)Math.Floor(localPosition);
            if (lowerIndex < 0 || lowerIndex + 1 >= source.Length)
            {
                break;
            }

            var fraction = (float)(localPosition - lowerIndex);
            samples.Add(source[lowerIndex] + ((source[lowerIndex + 1] - source[lowerIndex]) * fraction));
            _nextOutputSourcePosition += (double)format.SampleRate / TargetSampleRate;
        }

        _sourceFramesProcessed = endSourceIndex;
        _lastSample = mono[^1];
        _hasLastSample = true;
        return samples.ToArray();
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

    private static int Read24BitPcm(byte[] buffer, int offset)
    {
        var value = buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16);
        return (value & 0x0080_0000) == 0 ? value : value | unchecked((int)0xff00_0000);
    }
}
