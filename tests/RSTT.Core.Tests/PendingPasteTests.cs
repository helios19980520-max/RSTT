using RSTT.Core.Models;
using RSTT.Core.Transcription;
using Xunit;

namespace RSTT.Core.Tests;

public sealed class PendingPasteTests
{
    [Fact]
    public void UnpastedWordsRemainRevisableAfterRepeatedPartials()
    {
        var stabilizer = new TranscriptStabilizer(new TextFormattingPolicy(), new FinalOnlyCommitPolicy());
        var buffer = new PendingPasteBuffer();
        buffer.Apply(stabilizer.Process(Hypothesis("Subsequent breakthroughs in RTX and")));
        buffer.Apply(stabilizer.Process(Hypothesis("Subsequent breakthroughs in RTX and the")));
        buffer.Apply(stabilizer.Process(Hypothesis("Subsequent breakthroughs in RTX have enabled the leap to path tracing.")));
        Assert.Empty(buffer.Snapshot().Text);
        Assert.Equal("Subsequent breakthroughs in RTX have enabled the leap to path tracing.", buffer.Snapshot().Partial);
        buffer.Apply(stabilizer.CommitPending()!);
        Assert.Equal("Subsequent breakthroughs in RTX have enabled the leap to path tracing.", buffer.Snapshot().Text);
    }

    [Fact]
    public void ReplacedPasteAnchorDoesNotSuppressTheRestOfTheUtterance()
    {
        var stabilizer = new TranscriptStabilizer(new TextFormattingPolicy(), new FinalOnlyCommitPolicy());
        var buffer = new PendingPasteBuffer();
        buffer.Apply(stabilizer.Process(Hypothesis("We use CUDA")));
        buffer.Apply(stabilizer.CommitPending()!);
        buffer.Consume(buffer.Snapshot().ThroughId);
        buffer.Apply(stabilizer.Process(Hypothesis("We use graphics for accurate recognition every day.", true)));
        Assert.Equal("for accurate recognition every day.", buffer.Snapshot().Text);
    }

    [Fact]
    public void PasteFreezesPreviewWithoutRepeatingItWhenFinalArrives()
    {
        var stabilizer = new TranscriptStabilizer(new TextFormattingPolicy());
        var buffer = new PendingPasteBuffer();
        buffer.Apply(stabilizer.Process(Hypothesis("First paragraph.")));
        buffer.Apply(stabilizer.CommitPending()!);
        var first = buffer.Snapshot();
        Assert.Equal("First paragraph.", first.Text);
        Assert.Empty(first.Partial);

        buffer.Apply(stabilizer.Process(Hypothesis("First paragraph. Second paragraph.", true)));
        buffer.Consume(first.ThroughId);
        Assert.Equal("Second paragraph.", buffer.Snapshot().Text);
        var second = buffer.Snapshot();
        buffer.Consume(second.ThroughId);
        Assert.Empty(buffer.Snapshot().Text);
    }

    [Fact]
    public void FailedPasteKeepsSnapshotAndLaterWords()
    {
        var stabilizer = new TranscriptStabilizer(new TextFormattingPolicy());
        var buffer = new PendingPasteBuffer();
        buffer.Apply(stabilizer.Process(Hypothesis("First sentence.", true)));
        var attempted = buffer.Snapshot();
        buffer.Apply(stabilizer.Process(Hypothesis("Second sentence.", true)));
        Assert.Equal("First sentence. Second sentence.", buffer.Snapshot().Text);
        buffer.Consume(attempted.ThroughId);
        Assert.Equal("Second sentence.", buffer.Snapshot().Text);
    }

    [Fact]
    public void LongUnpastedTranscriptIsNotLostToCaptionHistoryLimit()
    {
        var stabilizer = new TranscriptStabilizer(new TextFormattingPolicy());
        var buffer = new PendingPasteBuffer();
        var text = string.Join(' ', Enumerable.Repeat("recognition", 1000));
        buffer.Apply(stabilizer.Process(Hypothesis(text, true)));
        Assert.Equal(text, buffer.Snapshot().Text);
    }

    [Fact]
    public void ClearingMidUtteranceDoesNotResurrectClearedWords()
    {
        var stabilizer = new TranscriptStabilizer(new TextFormattingPolicy());
        var buffer = new PendingPasteBuffer();
        buffer.Apply(stabilizer.Process(Hypothesis("Discard this.")));
        _ = stabilizer.CommitPending();
        buffer.Clear();
        buffer.Apply(stabilizer.Process(Hypothesis("Discard this. Keep this.", true)));
        Assert.Equal("Keep this.", buffer.Snapshot().Text);
    }

    [Fact]
    public void PendingTextSurvivesARecognitionRestart()
    {
        var stabilizer = new TranscriptStabilizer(new TextFormattingPolicy());
        var buffer = new PendingPasteBuffer();
        buffer.Apply(stabilizer.Process(Hypothesis("Before restart.", true)));
        stabilizer.Reset();
        buffer.Apply(stabilizer.Process(Hypothesis("After restart.", true)));
        Assert.Equal("Before restart. After restart.", buffer.Snapshot().Text);
    }

    private static RecognitionHypothesis Hypothesis(string text, bool final = false) =>
        new(new SessionGenerationId(1), 1, text, final, DateTimeOffset.UtcNow, "en", "test");
}
