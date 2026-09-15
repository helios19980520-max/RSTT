using RSTT.Core.Models;
using RSTT.Core.Workers;

namespace RSTT.Core.Compute;

public static class RecognitionSchedulingPolicy
{
    // Decoder blanks are not acoustic silence. Resetting on them loses words in
    // buffered transducers (including their right context). Keep the stream until
    // Stop; partial text remains live and can be pasted without resetting ASR.
    public static WorkerStreamingPolicy Streaming(RecognitionMode mode) => mode switch
    {
        RecognitionMode.Accuracy => new(1.2f, float.MaxValue),
        RecognitionMode.LowLatency => new(0.6f, float.MaxValue),
        RecognitionMode.LowPower => new(1.2f, float.MaxValue),
        _ => new(1.0f, float.MaxValue),
    };

    public static WorkerVadPolicy Vad(RecognitionMode mode, WorkerVadPolicy policy) =>
        policy with
        {
            PostRollMs = mode == RecognitionMode.LowLatency ? Math.Min(policy.PostRollMs, 500) : policy.PostRollMs,
            MaximumSegmentMs = Math.Min(policy.MaximumSegmentMs, mode switch
            {
                RecognitionMode.Accuracy => 20000,
                RecognitionMode.LowLatency => 8000,
                RecognitionMode.LowPower => 20000,
                _ => 16000,
            }),
        };

    public static IReadOnlyList<ComputeBackend> Candidates(ComputeBackend requested, bool cudaSupported) =>
        requested == ComputeBackend.Cpu || !cudaSupported
            ? [ComputeBackend.Cpu]
            : [ComputeBackend.Cuda, ComputeBackend.Cpu];
}
