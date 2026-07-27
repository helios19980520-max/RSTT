namespace RSTT.Core.Models;

/// <summary>One in-memory, mono 16 kHz PCM-float audio block.</summary>
public sealed record AudioChunk(long SequenceNumber, DateTimeOffset CapturedAt, float[] Samples)
{
    public const int SampleRate = 16_000;
}
