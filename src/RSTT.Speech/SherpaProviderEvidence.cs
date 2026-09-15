using System.Globalization;

namespace RSTT.Speech;

/// <summary>Accepts only ORT's node-assignment records, never requested-provider banners.</summary>
internal sealed class SherpaProviderEvidence
{
    private int _cudaNodes;
    private int _cpuNodes;
    private int _cudaSessions;
    private readonly Queue<string> _diagnostics = new();
    public string FailureDetail { get { lock (_diagnostics) return string.Join(" ", _diagnostics); } }
    public TaskCompletionSource WarmupCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Observe(string line)
    {
        if (line == "RSTT-WARMUP-COMPLETE") { WarmupCompleted.TrySetResult(); return; }
        if (!string.IsNullOrWhiteSpace(line) && !line.Contains("[V:onnxruntime", StringComparison.Ordinal) &&
            !line.Contains("[I:onnxruntime", StringComparison.Ordinal))
        {
            lock (_diagnostics)
            {
                _diagnostics.Enqueue(line.Length > 400 ? line[..400] : line);
                while (_diagnostics.Count > 8) _diagnostics.Dequeue();
            }
        }
        if (!line.Contains("VerifyEachNodeIsAssignedToAnEp", StringComparison.Ordinal)) return;
        var marker = line.IndexOf("Number of nodes: ", StringComparison.Ordinal);
        if (marker < 0) return;
        var digits = new string(line[(marker + 17)..].TakeWhile(char.IsAsciiDigit).ToArray());
        if (!int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var nodes)) return;
        if (line.Contains("placed on [CUDAExecutionProvider]", StringComparison.Ordinal))
        {
            _cudaNodes += nodes;
            _cudaSessions++;
        }
        else if (line.Contains("placed on [CPUExecutionProvider]", StringComparison.Ordinal)) _cpuNodes += nodes;
    }

    public string Verify()
    {
        if (_cudaNodes == 0)
            throw new InvalidOperationException(
                "The native runtime did not assign any model nodes to CUDA. " +
                "The matching CUDA Accelerator Pack is required; CPU fallback will be explicit. " + FailureDetail);
        return $"Verified ORT placement and warmup: {_cudaNodes} CUDA nodes in {_cudaSessions} sessions; {_cpuNodes} CPU nodes (unsupported/shape/control operations).";
    }
}
