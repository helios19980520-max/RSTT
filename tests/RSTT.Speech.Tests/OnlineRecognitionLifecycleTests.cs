using RSTT.Speech;
using Xunit;

namespace RSTT.Speech.Tests;

public sealed class OnlineRecognitionLifecycleTests
{
    [Fact]
    public void EndpointReadsAndEmitsTerminalTextBeforeReset()
    {
        var events = new List<string>();
        var facade = new FakeOnlineRecognizerFacade(events, "final word");

        OnlineRecognitionLifecycle.EmitEndpointThenReset(
            facade,
            text => events.Add($"emit:{text}"));

        Assert.Equal(
            ["result", "emit:final word", "reset"],
            events);
    }

    [Fact]
    public void SessionCompletionCallsInputFinishedThenDrainsAndEmits()
    {
        var events = new List<string>();
        var facade = new FakeOnlineRecognizerFacade(events, "complete tail", readyCount: 2);

        OnlineRecognitionLifecycle.FinishInputAndEmit(
            facade,
            text => events.Add($"emit:{text}"));

        Assert.Equal(
            ["input-finished", "ready", "decode", "ready", "decode", "ready", "result", "emit:complete tail"],
            events);
    }

    private sealed class FakeOnlineRecognizerFacade : IOnlineRecognizerFacade
    {
        private readonly List<string> _events;
        private readonly string _text;
        private int _readyCount;

        public FakeOnlineRecognizerFacade(
            List<string> events,
            string text,
            int readyCount = 0)
        {
            _events = events;
            _text = text;
            _readyCount = readyCount;
        }

        public void InputFinished() => _events.Add("input-finished");

        public bool IsReady()
        {
            _events.Add("ready");
            return _readyCount-- > 0;
        }

        public void Decode() => _events.Add("decode");

        public string GetResultText()
        {
            _events.Add("result");
            return _text;
        }

        public void Reset() => _events.Add("reset");
    }
}
