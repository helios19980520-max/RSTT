using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using RSTT.App.Services;
using RSTT.Core.Abstractions;
using RSTT.Core.Models;
using RSTT.Core.Settings;
using RSTT.Core.State;
using RSTT.Core.Transcription;
using Xunit;

namespace RSTT.Speech.Tests;

public sealed class RecognitionCoordinatorLifecycleTests
{
    [Fact]
    public async Task StopDrainsAudioBeforeInputFinishedAndInjectsTerminalCommit()
    {
        var events = new ConcurrentQueue<string>();
        var capture = new FakeCapture(events);
        var engine = new FakeEngine(events);
        var injection = new FakeInjection(events);
        var settings = new FakeSettings();
        await using var coordinator = new RecognitionCoordinator(
            capture,
            engine,
            injection,
            settings,
            new ApplicationStateService(),
            new TranscriptStabilizer(
                new TextFormattingPolicy(),
                new FinalOnlyCommitPolicy()),
            new CaptionHistory(),
            NullPerformanceMonitor.Instance,
            NullLogger<RecognitionCoordinator>.Instance);

        await coordinator.StartAsync();
        capture.Write(new AudioChunk(1, DateTimeOffset.UtcNow, new float[160]));
        capture.Write(new AudioChunk(2, DateTimeOffset.UtcNow, new float[160]));

        await coordinator.StopAsync();

        var ordered = events.ToArray();
        AssertBefore(ordered, "capture-stop", "input-finished");
        AssertBefore(ordered, "audio:1", "input-finished");
        AssertBefore(ordered, "audio:2", "input-finished");
        AssertBefore(ordered, "input-finished", "inject:terminal tail");
        Assert.False(coordinator.IsListening);
        Assert.Equal(TranscriptionSessionState.Stopped, coordinator.SessionState);
    }

    [Fact]
    public async Task DiagnosticsSelfTestUsesBalancedFramesAndTerminalResult()
    {
        var events = new ConcurrentQueue<string>();
        var engine = new FakeEngine(events);
        await using var coordinator = new RecognitionCoordinator(
            new FakeCapture(events),
            engine,
            new FakeInjection(events),
            new FakeSettings(),
            new ApplicationStateService(),
            new TranscriptStabilizer(
                new TextFormattingPolicy(),
                new FinalOnlyCommitPolicy()),
            new CaptionHistory(),
            NullPerformanceMonitor.Instance,
            NullLogger<RecognitionCoordinator>.Instance);

        var finalResults = await coordinator.RunSpeechSelfTestAsync(
            new float[AudioChunk.SampleRate * 11]);

        Assert.Equal(1, finalResults);
        var sampleCounts = engine.SampleCounts.ToArray();
        Assert.Equal(20, sampleCounts.Length);
        Assert.All(sampleCounts[..^1], count => Assert.Equal(8_960, count));
        Assert.Equal(5_760, sampleCounts[^1]);
    }

    private static void AssertBefore(string[] actual, string first, string second)
    {
        var firstIndex = Array.IndexOf(actual, first);
        var secondIndex = Array.IndexOf(actual, second);
        Assert.True(
            firstIndex >= 0 && secondIndex > firstIndex,
            $"Expected '{first}' before '{second}'. Actual: {string.Join(", ", actual)}");
    }

    private sealed class FakeCapture : IAudioCaptureService
    {
        private readonly ConcurrentQueue<string> _events;
        private Channel<AudioChunk> _chunks = CreateChannel();

        public FakeCapture(ConcurrentQueue<string> events)
        {
            _events = events;
        }

        public bool IsCapturing { get; private set; }

        public event EventHandler<AudioLevelEventArgs>? AudioLevelChanged
        {
            add { }
            remove { }
        }

        public Task<IReadOnlyList<AudioDevice>> GetOutputDevicesAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AudioDevice>>([]);

        public Task StartAsync(
            string? deviceId,
            CancellationToken cancellationToken = default)
        {
            _chunks = CreateChannel();
            IsCapturing = true;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            _events.Enqueue("capture-stop");
            IsCapturing = false;
            _chunks.Writer.TryComplete();
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<AudioChunk> ReadChunksAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var chunk in _chunks.Reader.ReadAllAsync(cancellationToken))
            {
                yield return chunk;
            }
        }

        public void Write(AudioChunk chunk) => Assert.True(_chunks.Writer.TryWrite(chunk));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static Channel<AudioChunk> CreateChannel() =>
            Channel.CreateUnbounded<AudioChunk>();
    }

    private sealed class FakeEngine : ISpeechRecognitionEngine
    {
        private readonly ConcurrentQueue<string> _events;
        private SessionGenerationId _generation;

        public FakeEngine(ConcurrentQueue<string> events)
        {
            _events = events;
        }

        public bool IsReady => true;

        public ConcurrentQueue<int> SampleCounts { get; } = new();

        public ModelInformation ModelInformation { get; } =
            new("fake", "Fake", "", true);

        public event EventHandler<RecognitionHypothesis>? RecognitionResultAvailable;

        public Task InitializeAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task ReloadAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task UnloadAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task StartAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task ProcessAudioAsync(
            AudioChunk chunk,
            CancellationToken cancellationToken = default)
        {
            _generation = chunk.SessionGenerationId;
            _events.Enqueue($"audio:{chunk.SequenceNumber}");
            SampleCounts.Enqueue(chunk.Samples.Length);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            _events.Enqueue("input-finished");
            RecognitionResultAvailable?.Invoke(
                this,
                new RecognitionHypothesis(
                    _generation,
                    2,
                    "terminal tail",
                    true,
                    DateTimeOffset.UtcNow,
                    "en",
                    "fake"));
            return Task.CompletedTask;
        }

        public Task ResetAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeInjection : ITextInjectionService
    {
        private readonly ConcurrentQueue<string> _events;

        public FakeInjection(ConcurrentQueue<string> events)
        {
            _events = events;
        }

        public Task<TextInjectionResult> InjectAsync(
            InjectionRequest request,
            CancellationToken cancellationToken = default)
        {
            _events.Enqueue($"inject:{request.Text}");
            return Task.FromResult(new TextInjectionResult(
                TextInjectionStatus.Success,
                request.Text.Length * 2,
                request.Text.Length * 2,
                request.Text.Length,
                (nint)1,
                42,
                0));
        }
    }

    private sealed class FakeSettings : ISettingsService
    {
        public AppSettings Current { get; } = new();

        public Task LoadAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task SaveAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
