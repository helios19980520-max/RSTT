/// <summary>Requests CUDA and exposes ORT's actual per-session node placement.</summary>
internal sealed class CudaExecutionVerification : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"rstt-cuda-{Guid.NewGuid():N}");

    public CudaExecutionVerification()
    {
        Directory.CreateDirectory(_directory);
        var config = Path.Combine(_directory, "provider.config");
        File.WriteAllText(config, "LogSeverityLevel=0\n");
        Provider = $"cuda:{config}";
    }

    public string Provider { get; }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
