using RSTT.Core.Models;
using RSTT.Core.Transcription;
using Xunit;

namespace RSTT.Core.Tests;

public sealed class TranscriptStabilizerTests
{
    private readonly TranscriptStabilizer _stabilizer = new(new TextFormattingPolicy());

    [Fact]
    public void StreamingPolicyRequiresConfirmationAndHoldsBackTwoWords()
    {
        Assert.Empty(_stabilizer.Process(Result("The quick brown fox")).Commits);

        var confirmed = _stabilizer.Process(Result("The quick brown fox"));

        Assert.Equal("The quick", confirmed.NewlyStableText);
        Assert.Equal("brown fox", confirmed.PendingText);
    }

    [Fact]
    public void GrowingHypothesesEmitOnlyNewCommitDeltas()
    {
        _stabilizer.Process(Result("The quick brown fox"));
        var first = _stabilizer.Process(Result("The quick brown fox"));
        _stabilizer.Process(Result("The quick brown fox jumps"));
        var second = _stabilizer.Process(Result("The quick brown fox jumps"));
        var final = _stabilizer.Process(Result("The quick brown fox jumps high", isFinal: true));

        Assert.Equal("The quick", first.NewlyStableText);
        Assert.Equal(" brown", second.NewlyStableText);
        Assert.Equal(" fox jumps high", final.NewlyStableText);
        Assert.Equal("The quick brown fox jumps high", final.StableText);
    }

    [Fact]
    public void RepeatedHypothesesNeverDuplicateACommit()
    {
        _stabilizer.Process(Result("hello wide world today"));
        var firstRepeat = _stabilizer.Process(Result("hello wide world today"));
        var secondRepeat = _stabilizer.Process(Result("hello wide world today"));
        var final = _stabilizer.Process(Result("hello wide world today", isFinal: true));

        Assert.Equal("hello wide", firstRepeat.NewlyStableText);
        Assert.Empty(secondRepeat.NewlyStableText);
        Assert.Equal(" world today", final.NewlyStableText);
        Assert.Equal("hello wide world today", final.StableText);
    }

    [Fact]
    public void RevisionCommitsOnlyWholeWordsInTheCommonPrefix()
    {
        _stabilizer.Process(Result("I really want two tickets now"));
        var revised = _stabilizer.Process(Result("I really want to buy tickets"));
        var final = _stabilizer.Process(Result("I really want to buy tickets", isFinal: true));

        Assert.Equal("I", revised.NewlyStableText);
        Assert.Equal(" really want to buy tickets", final.NewlyStableText);
        Assert.Equal("I really want to buy tickets", final.StableText);
    }

    [Fact]
    public void ShorterRevisionDoesNotCommitTheShortenedTail()
    {
        _stabilizer.Process(Result("hello wide world today"));
        var shortened = _stabilizer.Process(Result("hello wide"));

        Assert.Empty(shortened.NewlyStableText);
        Assert.Equal("hello wide", shortened.PendingText);
    }

    [Theory]
    [InlineData("turn on the kitchen light", "light")]
    [InlineData("send this to proper noun Codex", "Codex")]
    [InlineData("please keep the final word now", "now")]
    public void FinalResultUnconditionallyFlushesTheTail(string text, string finalWord)
    {
        _stabilizer.Process(Result(text));
        var partial = _stabilizer.Process(Result(text));
        var final = _stabilizer.Process(Result(text, isFinal: true));

        Assert.EndsWith(finalWord, final.NewlyStableText, StringComparison.Ordinal);
        Assert.Equal(text, final.StableText);
        Assert.True(final.Commits.Single().IsEndpointFinal);
    }

    [Fact]
    public void FinalResultPreservesUnicodePunctuationAndSurrogatePairs()
    {
        const string text = "café 日本語 한국어 🙂, right?";
        _stabilizer.Process(Result("café 日本語 한국어"));

        var final = _stabilizer.Process(Result(text, isFinal: true));

        Assert.Equal(text, final.NewlyStableText);
        Assert.Equal(text, final.StableText);
        Assert.Equal(text, string.Concat(final.Commits.Select(static commit => commit.Text)));
    }

    [Fact]
    public void CommitAndHypothesisCarryTheSameGenerationAndSourceSequence()
    {
        var final = _stabilizer.Process(Result("exact text", isFinal: true, sequence: 42, generation: 7));

        var commit = Assert.Single(final.Commits);
        Assert.Equal(new SessionGenerationId(7), commit.SessionGenerationId);
        Assert.Equal(42, commit.SourceSequenceId);
        Assert.Equal("exact text", commit.Text);
    }

    [Fact]
    public void EmptyResultsAndResetDoNotLeaveCommittedState()
    {
        var empty = _stabilizer.Process(Result(""));
        _stabilizer.Process(Result("hello"));
        _stabilizer.Reset();
        var afterReset = _stabilizer.Process(Result("hello", isFinal: true));

        Assert.Empty(empty.Commits);
        Assert.Equal("hello", afterReset.NewlyStableText);
        Assert.Equal("hello", afterReset.StableText);
    }

    [Fact]
    public void FinalResultStartsAFreshRecognitionSegment()
    {
        var first = _stabilizer.Process(Result("first segment", isFinal: true));
        var second = _stabilizer.Process(Result("second segment", isFinal: true, sequence: 2));

        Assert.Equal("first segment", first.NewlyStableText);
        Assert.Equal(" second segment", second.NewlyStableText);
        Assert.Equal("first segment second segment", second.StableText);
    }

    private static RecognitionHypothesis Result(
        string text,
        bool isFinal = false,
        long sequence = 1,
        long generation = 1) =>
        new(
            new SessionGenerationId(generation),
            sequence,
            text,
            isFinal,
            DateTimeOffset.UtcNow,
            "en",
            "test-engine");
}
