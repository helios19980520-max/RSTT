using NAudio.Wave;
using NAudio.Dsp;

namespace RSTT.Audio;

/// <summary>
/// Converts capture callbacks to mono with a persistent, anti-aliased resampler.
/// </summary>
internal sealed class Pcm16kMonoConverter
{
    private const int TargetSampleRate = 16_000;
    private int _sourceSampleRate;
    private int _sourceChannels;
    private readonly WdlResampler _resampler = new();

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

        _resampler.ResamplePrepare(frameCount, 1, out var input, out var inputOffset);
        for (var frame = 0; frame < frameCount; frame++)
        {
            input[inputOffset + frame] = ReadMonoFrame(buffer, frame, format);
        }
        var samples = new float[(int)Math.Ceiling(frameCount * (double)TargetSampleRate / format.SampleRate) + 128];
        var count = _resampler.ResampleOut(samples, 0, frameCount, samples.Length, 1);
        Array.Resize(ref samples, count);
        return samples;
    }

    public void Reset()
    {
        _sourceSampleRate = 0;
        _sourceChannels = 0;
        _resampler.Reset();
    }

    private void Reset(int sampleRate, int channels)
    {
        _sourceSampleRate = sampleRate;
        _sourceChannels = channels;
        _resampler.Reset();
        _resampler.SetMode(true, 2, true, 64, 32);
        _resampler.SetFeedMode(true);
        _resampler.SetRates(sampleRate, TargetSampleRate);
    }

    private static float ReadSample(byte[] buffer, int offset, WaveFormat format)
    {
        if ((format.Encoding == WaveFormatEncoding.IeeeFloat ||
             format is WaveFormatExtensible { SubFormat: var subFormat } && subFormat == new Guid("00000003-0000-0010-8000-00aa00389b71")) && format.BitsPerSample == 32)
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

    private static float ReadMonoFrame(byte[] buffer, int frameIndex, WaveFormat format)
    {
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
