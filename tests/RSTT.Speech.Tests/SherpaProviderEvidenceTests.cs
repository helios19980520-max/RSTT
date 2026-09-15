using Xunit;

namespace RSTT.Speech.Tests;

public sealed class SherpaProviderEvidenceTests
{
    [Theory]
    [InlineData("Available providers: CUDAExecutionProvider")]
    [InlineData("Provider=cuda")]
    [InlineData("VerifyEachNodeIsAssignedToAnEp All nodes placed on [CPUExecutionProvider]. Number of nodes: 120")]
    public void BannersAndCpuFallbackCannotClaimCuda(string log)
    {
        var evidence = new SherpaProviderEvidence();
        evidence.Observe(log);
        Assert.Throws<InvalidOperationException>(() => evidence.Verify());
    }

    [Fact]
    public void ReportsMixedPlacementAcrossModelSessions()
    {
        var evidence = new SherpaProviderEvidence();
        evidence.Observe("VerifyEachNodeIsAssignedToAnEp Node(s) placed on [CUDAExecutionProvider]. Number of nodes: 20");
        evidence.Observe("VerifyEachNodeIsAssignedToAnEp Node(s) placed on [CPUExecutionProvider]. Number of nodes: 3");
        evidence.Observe("VerifyEachNodeIsAssignedToAnEp All nodes placed on [CUDAExecutionProvider]. Number of nodes: 10");
        evidence.Observe("RSTT-WARMUP-COMPLETE");
        Assert.True(evidence.WarmupCompleted.Task.IsCompleted);
        Assert.Contains("30 CUDA nodes in 2 sessions; 3 CPU nodes", evidence.Verify());
    }
}
