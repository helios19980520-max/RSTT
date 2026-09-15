using RSTT.Core.Compute;
using RSTT.Core.Models;
using RSTT.Core.Workers;
using Xunit;

namespace RSTT.Core.Tests;

public sealed class RecognitionSchedulingPolicyTests
{
    [Fact]
    public void ExplicitCudaAttemptsGpuBeforeCpuFallback()
    {
        Assert.Equal(new[] { ComputeBackend.Cuda, ComputeBackend.Cpu }, RecognitionSchedulingPolicy.Candidates(ComputeBackend.Cuda, true));
        Assert.Equal(new[] { ComputeBackend.Cpu }, RecognitionSchedulingPolicy.Candidates(ComputeBackend.Cpu, true));
    }

    [Fact]
    public void AccuracyPreservesDecoderContextAndOfflineVadContext()
    {
        var accuracy = RecognitionSchedulingPolicy.Streaming(RecognitionMode.Accuracy);
        var lowLatency = RecognitionSchedulingPolicy.Streaming(RecognitionMode.LowLatency);
        Assert.False(accuracy.EnableEndpoint);
        Assert.True(accuracy.TrailingSilenceSeconds > lowLatency.TrailingSilenceSeconds);
        Assert.Equal(20000, RecognitionSchedulingPolicy.Vad(RecognitionMode.Accuracy, new WorkerVadPolicy()).MaximumSegmentMs);
        Assert.Equal(2400, accuracy.TailPaddingMs);
    }
}
