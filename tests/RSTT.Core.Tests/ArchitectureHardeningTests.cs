using RSTT.Core.Compute;
using RSTT.Core.Hotkeys;
using RSTT.Core.Models;
using RSTT.Core.State;
using RSTT.Core.Transcription;
using Xunit;

namespace RSTT.Core.Tests;

public sealed class ArchitectureHardeningTests
{
    [Theory]
    [InlineData("Ctrl+Alt+R", HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x52u)]
    [InlineData("Win+Shift+F12", HotkeyModifiers.Windows | HotkeyModifiers.Shift, 0x7Bu)]
    [InlineData("Ctrl+PageDown", HotkeyModifiers.Control, 0x22u)]
    public void HotkeyParserProducesRegisterHotKeyValues(
        string value,
        HotkeyModifiers expectedModifiers,
        uint expectedVirtualKey)
    {
        var parsed = HotkeyGestureParser.TryParse(
            value,
            out var gesture,
            out var error);

        Assert.True(parsed, error);
        Assert.Equal(
            expectedModifiers | HotkeyModifiers.NoRepeat,
            gesture.Modifiers);
        Assert.Equal(expectedVirtualKey, gesture.VirtualKey);
    }

    [Theory]
    [InlineData("")]
    [InlineData("R")]
    [InlineData("Ctrl+Alt+VolumeUp")]
    [InlineData("Ctrl+R+T")]
    public void HotkeyParserRejectsAmbiguousOrUnsupportedValues(string value)
    {
        Assert.False(HotkeyGestureParser.TryParse(value, out _, out var error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void SessionStateMachineRejectsOutOfOrderTransitions()
    {
        var state = new TranscriptionSessionStateMachine();

        Assert.Throws<InvalidOperationException>(
            () => state.TransitionTo(TranscriptionSessionState.Listening));

        state.TransitionTo(TranscriptionSessionState.Starting);
        state.TransitionTo(TranscriptionSessionState.StartingAudio);
        state.TransitionTo(TranscriptionSessionState.Listening);
        state.TransitionTo(TranscriptionSessionState.Stopping);
        state.TransitionTo(TranscriptionSessionState.Stopped);

        Assert.Equal(TranscriptionSessionState.Stopped, state.Current);
    }

    [Fact]
    public void ComputeAutoFallsBackWithoutClaimingCudaExecution()
    {
        var model = new ModelDescriptor
        {
            Id = "cuda-capable-model",
            DisplayName = "CUDA-capable model",
            CudaSupported = true,
        };
        var probes = new[]
        {
            new ComputeBackendProbe(ComputeBackend.Cpu, true, "CPU ready"),
            new ComputeBackendProbe(
                ComputeBackend.Cuda,
                false,
                "NVIDIA adapter found; validated runtime unavailable"),
        };

        var selection = ComputeSelectionPolicy.Select(
            ComputeBackend.Auto,
            model,
            probes);

        Assert.Equal(ComputeBackend.Cpu, selection.Selected);
        Assert.True(selection.FellBack);
        Assert.Contains("runtime unavailable", selection.Reason);
    }

    [Fact]
    public void ComputeAutoSelectsCudaOnlyAfterAValidatedProbe()
    {
        var model = new ModelDescriptor
        {
            Id = "cuda-model",
            DisplayName = "CUDA model",
            CudaSupported = true,
        };
        var probes = new[]
        {
            new ComputeBackendProbe(ComputeBackend.Cpu, true, "CPU ready"),
            new ComputeBackendProbe(ComputeBackend.Cuda, true, "CUDA warmup passed"),
        };

        var selection = ComputeSelectionPolicy.Select(
            ComputeBackend.Auto,
            model,
            probes);

        Assert.Equal(ComputeBackend.Cuda, selection.Selected);
        Assert.False(selection.FellBack);
        Assert.Contains("warmup passed", selection.Reason);
    }

    [Fact]
    public void ExplicitCpuWinsEvenWhenCudaIsAvailable()
    {
        var model = new ModelDescriptor
        {
            Id = "cuda-model",
            DisplayName = "CUDA model",
            CudaSupported = true,
        };
        var probes = new[]
        {
            new ComputeBackendProbe(ComputeBackend.Cpu, true, "CPU selected"),
            new ComputeBackendProbe(ComputeBackend.Cuda, true, "CUDA ready"),
        };

        var selection = ComputeSelectionPolicy.Select(
            ComputeBackend.Cpu,
            model,
            probes);

        Assert.Equal(ComputeBackend.Cpu, selection.Selected);
        Assert.False(selection.FellBack);
    }

    [Fact]
    public void ExplicitUnavailableCudaFallsBackWithAReason()
    {
        var model = new ModelDescriptor
        {
            Id = "cuda-model",
            DisplayName = "CUDA model",
            CudaSupported = true,
        };
        var probes = new[]
        {
            new ComputeBackendProbe(ComputeBackend.Cpu, true, "CPU ready"),
            new ComputeBackendProbe(
                ComputeBackend.Cuda,
                false,
                "CUDA dependencies missing"),
        };

        var selection = ComputeSelectionPolicy.Select(
            ComputeBackend.Cuda,
            model,
            probes);

        Assert.Equal(ComputeBackend.Cpu, selection.Selected);
        Assert.True(selection.FellBack);
        Assert.Contains("dependencies missing", selection.Reason);
    }

    [Fact]
    public void CaptionHistoryReplacesPartialAndBoundsFinalSegments()
    {
        var history = new CaptionHistory(5);
        history.Apply(new TranscriptUpdate(
            "",
            "hello every",
            "",
            false,
            "hello every",
            Sequence: 1));
        history.Apply(new TranscriptUpdate(
            "",
            "hello everyone",
            "",
            false,
            "hello everyone",
            Sequence: 2));

        var partial = history.Snapshot();
        Assert.Empty(partial.FinalSegments);
        Assert.Equal("hello everyone", partial.CurrentPartial?.Text);
        Assert.Equal(2, partial.CurrentPartial?.Sequence);

        for (var index = 0; index < 7; index++)
        {
            history.Apply(new TranscriptUpdate(
                "",
                "",
                "",
                true,
                $"final {index}",
                $"final {index}",
                index + 3));
        }

        var finals = history.Snapshot();
        Assert.Equal(5, finals.FinalSegments.Count);
        Assert.Equal("final 2", finals.FinalSegments[0].Text);
        Assert.Equal("final 6", finals.FinalSegments[^1].Text);
        Assert.Null(finals.CurrentPartial);
    }

    [Fact]
    public void FinalOnlyPolicyNeverCommitsAPartial()
    {
        var policy = new FinalOnlyCommitPolicy();

        Assert.Empty(policy.GetStablePrefix("hello", "hello world", false));
        Assert.Equal(
            "hello world",
            policy.GetStablePrefix("hello", "hello world", true));
    }

    [Fact]
    public void ControlledRecoveryClearsOnlyRevisableTranscriptState()
    {
        var stabilizer = new TranscriptStabilizer(
            new TextFormattingPolicy(),
            new FinalOnlyCommitPolicy());
        stabilizer.Process(new RecognitionResult(
            "first sentence",
            true,
            1,
            DateTimeOffset.UtcNow));
        stabilizer.Process(new RecognitionResult(
            "unfinished fragment",
            false,
            2,
            DateTimeOffset.UtcNow));

        var preserved = stabilizer.ResetCurrentSegment();
        var next = stabilizer.Process(new RecognitionResult(
            "second sentence",
            true,
            3,
            DateTimeOffset.UtcNow));

        Assert.Equal("first sentence", preserved);
        Assert.Equal("first sentence second sentence", next.StableText);
        Assert.Equal(" second sentence", next.NewlyStableText);
    }

    [Fact]
    public void ControlledRecoveryClearsPartialCaptionButKeepsFinalHistory()
    {
        var history = new CaptionHistory();
        history.Apply(new TranscriptUpdate(
            "final",
            "",
            "final",
            true,
            "final",
            "final",
            1));
        history.Apply(new TranscriptUpdate(
            "final",
            "partial",
            "",
            false,
            "partial",
            Sequence: 2));

        var recovered = history.ClearPartial();

        Assert.Single(recovered.FinalSegments);
        Assert.Equal("final", recovered.FinalSegments[0].Text);
        Assert.Null(recovered.CurrentPartial);
    }

    [Fact]
    public void SessionTranscriptIsBoundedWithoutDroppingTheLatestInjectionDelta()
    {
        var stabilizer = new TranscriptStabilizer(
            new TextFormattingPolicy(),
            new FinalOnlyCommitPolicy());
        TranscriptUpdate latest = default!;
        for (var index = 0; index < 1_000; index++)
        {
            var text = $"segment {index:D4} contains enough text to grow the session history";
            latest = stabilizer.Process(new RecognitionResult(
                text,
                true,
                index,
                DateTimeOffset.UtcNow));
        }

        Assert.True(latest.StableText.Length <= 6_000);
        Assert.Contains("segment 0999", latest.StableText);
        Assert.Contains("segment 0999", latest.NewlyStableText);
        Assert.DoesNotContain("segment 0000", latest.StableText);
    }
}
