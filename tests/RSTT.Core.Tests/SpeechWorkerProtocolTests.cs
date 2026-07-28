using RSTT.Core.Workers;
using Xunit;

namespace RSTT.Core.Tests;

public sealed class SpeechWorkerProtocolTests
{
    [Fact]
    public async Task JsonFrameRoundTripsAcrossShortReads()
    {
        var request = new WorkerLoadRequest(@"C:\models\whisper.bin", "auto", 4);
        var frame = SpeechWorkerProtocol.Json(SpeechWorkerMessageType.Load, 42, request);
        await using var storage = new MemoryStream();
        await SpeechWorkerProtocol.WriteAsync(storage, frame);
        storage.Position = 0;
        await using var shortReads = new ShortReadStream(storage, 3);

        var received = Assert.IsType<SpeechWorkerFrame>(
            await SpeechWorkerProtocol.ReadAsync(shortReads));
        var decoded = SpeechWorkerProtocol.DeserializeJson<WorkerLoadRequest>(received);

        Assert.Equal(SpeechWorkerMessageType.Load, received.Type);
        Assert.Equal(42, received.CorrelationId);
        Assert.Equal(request, decoded);
    }

    [Fact]
    public void AudioFrameIsLittleEndianAndLossless()
    {
        float[] samples = [0f, -0.5f, 1f, float.Epsilon];

        var frame = SpeechWorkerProtocol.Audio(7, samples);
        var decoded = SpeechWorkerProtocol.DeserializeAudio(frame);

        Assert.Equal(samples, decoded);
        Assert.Equal(SpeechWorkerMessageType.Audio, frame.Type);
        Assert.Equal(7, frame.CorrelationId);
    }

    [Fact]
    public async Task CleanEndReturnsNullButTruncatedHeaderFails()
    {
        await using var empty = new MemoryStream();
        Assert.Null(await SpeechWorkerProtocol.ReadAsync(empty));

        await using var truncated = new MemoryStream(new byte[5]);
        await Assert.ThrowsAsync<EndOfStreamException>(
            async () => await SpeechWorkerProtocol.ReadAsync(truncated));
    }

    private sealed class ShortReadStream(Stream inner, int maximumRead) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position
        {
            get => inner.Position;
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) =>
            inner.Read(buffer, offset, Math.Min(count, maximumRead));

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer[..Math.Min(buffer.Length, maximumRead)], cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
