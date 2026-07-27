using RSTT.Core.Transcription;
using Xunit;

namespace RSTT.Core.Tests;

public sealed class TextFormattingPolicyTests
{
    private readonly TextFormattingPolicy _policy = new();

    [Theory]
    [InlineData("  hello   world  ", "hello world")]
    [InlineData("hello , world !", "hello, world!")]
    [InlineData("( spaced )", "(spaced)")]
    [InlineData(null, "")]
    public void NormalizeProducesSafeHumanSpacing(string? input, string expected) =>
        Assert.Equal(expected, _policy.Normalize(input));

    [Theory]
    [InlineData("hello", "world", "hello world")]
    [InlineData("hello", ".", "hello.")]
    [InlineData("open (", "value", "open (value")]
    [InlineData("", "first", "first")]
    public void AppendDoesNotDuplicateOrDamagePunctuation(string existing, string addition, string expected) =>
        Assert.Equal(expected, _policy.Append(existing, addition));
}
