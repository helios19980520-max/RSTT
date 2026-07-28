using RSTT.Speech;
using Xunit;

namespace RSTT.Speech.Tests;

public sealed class WorkerExecutableResolverTests
{
    [Fact]
    public void ReleaseResolutionNeverEscapesApplicationDirectory()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"rstt-worker-resolution-{Guid.NewGuid():N}");
        try
        {
            var appDirectory = Path.Combine(
                root,
                "artifacts",
                "publish",
                "cpu-only");
            var developmentWorker = Path.Combine(
                root,
                "src",
                "RSTT.Sherpa.Cuda12.Worker",
                "bin",
                "Debug",
                "net8.0-windows",
                "win-x64",
                "RSTT.Speech.Worker.exe");
            Directory.CreateDirectory(appDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(developmentWorker)!);
            File.WriteAllText(Path.Combine(root, "RSTT.sln"), string.Empty);
            File.WriteAllText(developmentWorker, string.Empty);

            var resolved = WorkerExecutableResolver.Resolve(
                appDirectory,
                Path.Combine("workers", "sherpa-cuda12", "1.13.4"),
                "RSTT.Speech.Worker.exe",
                "RSTT.Sherpa.Cuda12.Worker",
                allowDevelopmentFallback: false);

            Assert.Equal(
                Path.Combine(
                    appDirectory,
                    "workers",
                    "sherpa-cuda12",
                    "1.13.4",
                    "RSTT.Speech.Worker.exe"),
                resolved);
            Assert.NotEqual(developmentWorker, resolved);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
