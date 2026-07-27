using RSTT.Core.Models;
using RSTT.Core.Transcription;
using Xunit;

namespace RSTT.Core.Tests;

public sealed class TranscriptStabilizerTests
{
    private readonly TranscriptStabilizer _stabilizer = new(new TextFormattingPolicy());

    [Fact]
    public void GrowingHypothesisEmitsOnlyNewlyConfirmedText()
    {
        _stabilizer.Process(Result("The quick"));
        var second = _stabilizer.Process(Result("The quick brown"));
        var third = _stabilizer.Process(Result("The quick brown fox"));
        var final = _stabilizer.Process(Result("The quick brown fox jumps", isFinal: true));

        Assert.Equal("The quick", second.NewlyStableText);
        Assert.Equal(" brown", third.NewlyStableText);
        Assert.Equal(" fox jumps", final.NewlyStableText);
        Assert.Equal("The quick brown fox jumps", final.StableText);
    }

    [Fact]
    public void RepeatedHypothesesDoNotDuplicateInjectedText()
    {
        _stabilizer.Process(Result("hello world"));
        var firstRepeat = _stabilizer.Process(Result("hello world"));
        var secondRepeat = _stabilizer.Process(Result("hello world"));
        var final = _stabilizer.Process(Result("hello world", isFinal: true));

        Assert.Equal("hello world", firstRepeat.NewlyStableText);
        Assert.Empty(secondRepeat.NewlyStableText);
        Assert.Empty(final.NewlyStableText);
        Assert.Equal("hello world", final.StableText);
    }

    [Fact]
    public void RevisionConfirmsOnlyTheSafePrefix()
    {
        _stabilizer.Process(Result("I want two"));
        var revised = _stabilizer.Process(Result("I want to buy"));
        var final = _stabilizer.Process(Result("I want to buy", isFinal: true));

        Assert.Equal("I want", revised.NewlyStableText);
        Assert.Equal(" to buy", final.NewlyStableText);
        Assert.Equal("I want to buy", final.StableText);
    }

    [Fact]
    public void ShorterRevisionDoesNotConfirmAShortenedPartial()
    {
        _stabilizer.Process(Result("hello world"));
        var shortened = _stabilizer.Process(Result("hello"));

        Assert.Empty(shortened.NewlyStableText);
        Assert.Equal("hello", shortened.PendingText);
    }

    [Fact]
    public void FinalResultFlushesPendingUnicodeAndPunctuation()
    {
        _stabilizer.Process(Result("we're testing café"));
        var final = _stabilizer.Process(Result("we're testing café, right ?", isFinal: true));

        Assert.Equal("we're testing café, right?", final.NewlyStableText);
        Assert.Equal("we're testing café, right?", final.StableText);
    }

    [Fact]
    public void EmptyResultsAndResetDoNotLeaveInjectedState()
    {
        var empty = _stabilizer.Process(Result(""));
        _stabilizer.Process(Result("hello"));
        _stabilizer.Process(Result("hello"));
        _stabilizer.Reset();
        var afterReset = _stabilizer.Process(Result("hello", isFinal: true));

        Assert.Empty(empty.NewlyStableText);
        Assert.Equal("hello", afterReset.NewlyStableText);
        Assert.Equal("hello", afterReset.StableText);
    }

    [Fact]
    public void RevisionAfterConfirmedTypoDoesNotRepeatTheCorrectedTail()
    {
        const string first = "The quick brown fox jumps over the laz y dog";
        const string revised = "The quick brown fox jumps over the lazy dog. Real time recognition";

        _stabilizer.Process(Result(first));
        _stabilizer.Process(Result(first));
        _stabilizer.Process(Result(revised));
        var stableRevision = _stabilizer.Process(Result(revised));
        var final = _stabilizer.Process(Result(revised, isFinal: true));

        Assert.Equal(" Real time recognition", stableRevision.NewlyStableText);
        Assert.DoesNotContain("dog lazy dog", final.StableText, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(final.NewlyStableText);
    }

    [Fact]
    public void FinalResultStartsAFreshRecognitionSegment()
    {
        var first = _stabilizer.Process(Result("first segment", isFinal: true));
        _stabilizer.Process(Result("second segment"));
        var second = _stabilizer.Process(Result("second segment"));

        Assert.Equal("first segment", first.NewlyStableText);
        Assert.Equal(" second segment", second.NewlyStableText);
        Assert.Equal("first segment second segment", second.StableText);
    }

    private static RecognitionResult Result(string text, bool isFinal = false) => new(text, isFinal, 1, DateTimeOffset.UtcNow);
}
