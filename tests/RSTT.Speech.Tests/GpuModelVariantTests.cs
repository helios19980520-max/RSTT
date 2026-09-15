using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RSTT.Core.Abstractions;
using RSTT.Core.Models;
using RSTT.Speech;
using Xunit;

namespace RSTT.Speech.Tests;

public sealed class GpuModelVariantTests
{
    [Fact]
    public async Task BackendSwitchUsesSeparateGpuWeightsAndRetainsCpuFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), $"rstt-gpu-selection-{Guid.NewGuid():N}");
        var cpuFile = new ModelArtifact { Key = "encoder", FileName = "encoder.int8.onnx", ExpectedBytes = 1 };
        var gpuFile = new ModelArtifact { Key = "encoder", FileName = "encoder.onnx", ExpectedBytes = 2 };
        var descriptor = new ModelDescriptor
        {
            Id = LocalModelManager.DefaultModelId, DisplayName = "Test", Engine = "online-transducer",
            DirectoryName = "cpu", Artifacts = [cpuFile], IsRecommended = true, FeatureDimension = 128,
            CudaSupported = true, CudaVariant = new("gpu", "gpu-revision", [gpuFile]),
        };
        try
        {
            foreach (var (directory, artifact, revision) in new[] { ("cpu", cpuFile, ""), ("gpu", gpuFile, "gpu-revision") })
            {
                var folder = Path.Combine(root, "Models", directory);
                Directory.CreateDirectory(folder);
                File.WriteAllBytes(Path.Combine(folder, artifact.FileName), new byte[artifact.ExpectedBytes]);
                File.WriteAllText(Path.Combine(folder, "model.json"), JsonSerializer.Serialize(new
                {
                    descriptor.Id, descriptor.Engine, Revision = revision, descriptor.FeatureDimension,
                    Files = new Dictionary<string, string> { [artifact.Key] = artifact.FileName }, NumThreads = 2,
                }));
            }
            using var manager = new LocalModelManager(new Paths(root), null, new Catalog(descriptor), NullLogger<LocalModelManager>.Instance);
            var gpu = await manager.GetSelectedInstallationAsync(ComputeBackend.Cuda);
            Assert.EndsWith("gpu" + Path.DirectorySeparatorChar + "encoder.onnx", gpu.Files["encoder"], StringComparison.Ordinal);
            Assert.Equal(descriptor.Id, gpu.Information.Id);
            var cpu = await manager.GetSelectedInstallationAsync(ComputeBackend.Cpu);
            Assert.EndsWith("cpu" + Path.DirectorySeparatorChar + "encoder.int8.onnx", cpu.Files["encoder"], StringComparison.Ordinal);
            Assert.True(File.Exists(gpu.Files["encoder"]));
            await manager.DeleteAsync(descriptor.Id);
            Assert.False(File.Exists(gpu.Files["encoder"]));
            Assert.False(File.Exists(cpu.Files["encoder"]));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class Catalog(ModelDescriptor model) : IModelCatalog
    {
        public IReadOnlyList<ModelDescriptor> GetModels() => [model];
        public ModelDescriptor GetById(string modelId) => model;
        public IReadOnlyList<string> Validate() => [];
    }
    private sealed class Paths(string root) : IAppPaths
    {
        public string RootDirectory => root;
        public string ModelsDirectory => Path.Combine(root, "Models");
        public string LogsDirectory => Path.Combine(root, "Logs");
        public string SettingsFilePath => Path.Combine(root, "settings.json");
        public void EnsureDirectoriesExist() { Directory.CreateDirectory(ModelsDirectory); Directory.CreateDirectory(LogsDirectory); }
    }
}
