using NAudio.Wave;
using RSTT.Audio;
using Xunit;

namespace RSTT.Speech.Tests;

public sealed class Pcm16kMonoConverterTests
{
    [Fact]
    public void FloatStereo48kIsDownmixedAndResampledTo16k()
    {
        var converter = new Pcm16kMonoConverter();
        var format = WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2);
        var buffer = CreateStereoFloatBuffer(480, 0.5f, 0.5f);

        var samples = converter.Process(buffer, buffer.Length, format);

        Assert.InRange(samples.Length, 159, 160);
        Assert.All(samples, sample => Assert.InRange(sample, 0.499f, 0.501f));
    }

    [Fact]
    public void ResamplerKeepsContinuityAcrossCaptureCallbacks()
    {
        var converter = new Pcm16kMonoConverter();
        var format = WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2);
        var first = converter.Process(CreateStereoFloatBuffer(480, 0.25f, 0.25f), 480 * format.BlockAlign, format);
        var secondBuffer = CreateStereoFloatBuffer(480, 0.25f, 0.25f);

        var second = converter.Process(secondBuffer, secondBuffer.Length, format);

        Assert.InRange(first.Length + second.Length, 319, 320);
        Assert.All(second, sample => Assert.InRange(sample, 0.249f, 0.251f));
    }

    private static byte[] CreateStereoFloatBuffer(int frameCount, float left, float right)
    {
        var buffer = new byte[frameCount * sizeof(float) * 2];
        for (var frame = 0; frame < frameCount; frame++)
        {
            BitConverter.GetBytes(left).CopyTo(buffer, frame * sizeof(float) * 2);
            BitConverter.GetBytes(right).CopyTo(buffer, (frame * sizeof(float) * 2) + sizeof(float));
        }

        return buffer;
    }
}
