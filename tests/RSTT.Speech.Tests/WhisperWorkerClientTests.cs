using RSTT.Core.Models;
using RSTT.Speech;
using Xunit;

namespace RSTT.Speech.Tests;

public sealed class WhisperWorkerClientTests
{
    [Fact]
    public async Task CpuWorkerStartsAndCompletesVersionedHandshake()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var worker = await WhisperWorkerClient.StartAsync(
            ComputeBackend.Cpu,
            timeout.Token,
            TestRepository.AppWorkerRoot);

        Assert.Equal(ComputeBackend.Cpu, worker.Backend);
        Assert.Equal("whisper.cpp", worker.Handshake.Engine);
        Assert.Equal("cpu", worker.Handshake.Backend);
        Assert.Equal(1, worker.Handshake.ProtocolVersion);
        Assert.Contains("Whisper.net", worker.Handshake.RuntimeVersion, StringComparison.Ordinal);
    }
}
