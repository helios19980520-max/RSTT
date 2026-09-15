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

        Assert.InRange(samples.Length, 140, 160);
        Assert.All(samples.Skip(32), sample => Assert.InRange(sample, 0.499f, 0.501f));
    }

    [Fact]
    public void ResamplerKeepsContinuityAcrossCaptureCallbacks()
    {
        var converter = new Pcm16kMonoConverter();
        var format = WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2);
        var first = converter.Process(CreateStereoFloatBuffer(480, 0.25f, 0.25f), 480 * format.BlockAlign, format);
        var secondBuffer = CreateStereoFloatBuffer(480, 0.25f, 0.25f);

        var second = converter.Process(secondBuffer, secondBuffer.Length, format);

        Assert.InRange(first.Length + second.Length, 300, 320);
        Assert.All(second, sample => Assert.InRange(sample, 0.249f, 0.251f));
    }

    [Fact]
    public void DownsamplingRejectsHighFrequenciesInsteadOfAliasingThemIntoSpeech()
    {
        var converter = new Pcm16kMonoConverter();
        var format = WaveFormat.CreateIeeeFloatWaveFormat(48000, 1);
        var source = Enumerable.Range(0, 48000).Select(i => (float)Math.Sin(2 * Math.PI * 12000 * i / 48000)).ToArray();
        var buffer = new byte[source.Length * sizeof(float)];
        Buffer.BlockCopy(source, 0, buffer, 0, buffer.Length);
        var samples = converter.Process(buffer, buffer.Length, format);
        var rms = Math.Sqrt(samples.Skip(64).Average(sample => sample * sample));
        Assert.True(rms < 0.01, $"Out-of-band tone leaked into speech at RMS {rms}.");
    }

    [Fact]
    public void CallbackBoundariesDoNotChangeResampledSpeech()
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(44100, 1);
        var source = Enumerable.Range(0, 44100).Select(i => (float)Math.Sin(2 * Math.PI * 1100 * i / 44100)).ToArray();
        var buffer = new byte[source.Length * sizeof(float)];
        Buffer.BlockCopy(source, 0, buffer, 0, buffer.Length);
        var whole = new Pcm16kMonoConverter().Process(buffer, buffer.Length, format);
        var incremental = new Pcm16kMonoConverter();
        var actual = new List<float>();
        foreach (var block in buffer.Chunk(441 * sizeof(float))) actual.AddRange(incremental.Process(block, block.Length, format));
        Assert.Equal(whole.Length, actual.Count);
        for (var index = 0; index < whole.Length; index++) Assert.InRange(Math.Abs(whole[index] - actual[index]), 0, 0.00001f);
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
