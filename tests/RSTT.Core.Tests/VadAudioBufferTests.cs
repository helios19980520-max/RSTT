using RSTT.Core.Workers;
using Xunit;

namespace RSTT.Core.Tests;

public sealed class VadAudioBufferTests
{
    [Fact]
    public void ExtractRestoresConfiguredPreAndPostRoll()
    {
        var buffer = new VadAudioBuffer(16_000, 200, 500, 20_000);
        var samples = Enumerable.Range(0, 24_000)
            .Select(index => (float)index)
            .ToArray();
        buffer.Append(samples);

        var segment = buffer.Extract(4_000, 8_000);

        Assert.Equal(19_200, segment.Length);
        Assert.Equal(800, segment[0]);
        Assert.Equal(19_999, segment[^1]);
    }

    [Fact]
    public void BufferRemainsBoundedAcrossLongSessions()
    {
        var buffer = new VadAudioBuffer(16_000, 200, 500, 20_000);
        var block = new float[1_600];
        for (var index = 0; index < 1_800; index++)
        {
            buffer.Append(block);
        }

        Assert.Equal(2_880_000, buffer.TotalSamples);
        Assert.InRange(buffer.Count, 1, 347_200);
    }
}
