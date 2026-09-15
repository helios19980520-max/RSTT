using RSTT.Audio;
using RSTT.Core.Models;
using Xunit;

namespace RSTT.Speech.Tests;

public sealed class AudioChunkQueueTests
{
    [Fact]
    public async Task DecodeBurstCannotEvictTheStartOrMiddleOfSpeech()
    {
        var queue = new AudioChunkQueue();
        // A 560 ms model decode can block consumption for far more than the old
        // twelve 10 ms WASAPI packets. Every sample must survive that burst.
        for (var sequence = 0; sequence < 200; sequence++)
            queue.Write(new AudioChunk(sequence, DateTimeOffset.UtcNow, Enumerable.Repeat((float)sequence, 160).ToArray()));
        queue.Complete();
        Assert.Equal(200, queue.Count);
        Assert.True(queue.Reader.TryPeek(out var oldest));
        Assert.Equal(0, oldest.SequenceNumber);
        var received = new List<long>();
        await foreach (var chunk in queue.Reader.ReadAllAsync())
        {
            queue.Acknowledge(chunk);
            received.Add(chunk.SequenceNumber);
            Assert.All(chunk.Samples, sample => Assert.Equal((float)chunk.SequenceNumber, sample));
        }
        Assert.Equal(Enumerable.Range(0, 200).Select(i => (long)i), received);
        Assert.Equal(0, queue.QueuedSamples);
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public void SustainedOverloadFailsExplicitlyAndPreservesAlreadyQueuedSpeech()
    {
        var queue = new AudioChunkQueue();
        var first = new AudioChunk(1, DateTimeOffset.UtcNow, new float[60 * AudioChunk.SampleRate]);
        queue.Write(first);
        Assert.Throws<InvalidOperationException>(() => queue.Write(new AudioChunk(2, DateTimeOffset.UtcNow, [1f])));
        Assert.True(queue.Reader.TryRead(out var preserved));
        Assert.Same(first, preserved);
    }
}
