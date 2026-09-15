using System.Threading.Channels;
using RSTT.Core.Models;

namespace RSTT.Audio;

/// <summary>Preserves audio across decode bursts; overload is a visible failure, never an eviction.</summary>
internal sealed class AudioChunkQueue
{
    private readonly Channel<AudioChunk> _channel = Channel.CreateUnbounded<AudioChunk>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = true,
        AllowSynchronousContinuations = false,
    });
    private long _queuedSamples;
    private int _queuedChunks;
    public long QueuedSamples => Math.Max(0, Interlocked.Read(ref _queuedSamples));
    public int Count => Math.Max(0, Volatile.Read(ref _queuedChunks));
    public ChannelReader<AudioChunk> Reader => _channel.Reader;

    public void Write(AudioChunk chunk)
    {
        if (Interlocked.Add(ref _queuedSamples, chunk.Samples.Length) > AudioChunk.SampleRate * 60L)
        {
            Interlocked.Add(ref _queuedSamples, -chunk.Samples.Length);
            throw new InvalidOperationException("Recognition is more than 60 seconds behind. Capture stopped to protect the recorded speech. Select a faster model or GPU backend.");
        }
        Interlocked.Increment(ref _queuedChunks);
        if (!_channel.Writer.TryWrite(chunk))
        {
            Interlocked.Decrement(ref _queuedChunks);
            Interlocked.Add(ref _queuedSamples, -chunk.Samples.Length);
            throw new InvalidOperationException("The audio queue has already closed.");
        }
    }

    public void Complete(Exception? error = null) => _channel.Writer.TryComplete(error);
    public void Acknowledge(AudioChunk chunk)
    {
        Interlocked.Decrement(ref _queuedChunks);
        Interlocked.Add(ref _queuedSamples, -chunk.Samples.Length);
    }
}
