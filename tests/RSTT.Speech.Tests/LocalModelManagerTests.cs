using Microsoft.Extensions.Logging.Abstractions;
using RSTT.Core.Abstractions;
using RSTT.Core.Models;
using RSTT.Speech;
using Xunit;

namespace RSTT.Speech.Tests;

public sealed class LocalModelManagerTests
{
    [Fact]
    public void MissingManifestReportsModelMissingWithoutThrowing()
    {
        var root = Path.Combine(Path.GetTempPath(), $"rstt-tests-{Guid.NewGuid():N}");
        try
        {
            var manager = new LocalModelManager(new TestPaths(root), NullLogger<LocalModelManager>.Instance);

            var valid = manager.TryValidateModel(out var model);

            Assert.False(valid);
            Assert.False(model.IsInstalled);
            Assert.Contains("model.json", model.StatusMessage, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public void ManifestWithMissingDeclaredFileReportsTheExactFile()
    {
        var root = Path.Combine(Path.GetTempPath(), $"rstt-tests-{Guid.NewGuid():N}");
        try
        {
            var paths = new TestPaths(root);
            paths.EnsureDirectoriesExist();
            var manager = new LocalModelManager(paths, NullLogger<LocalModelManager>.Instance);
            var modelDirectory = manager.GetSelectedModel().Directory;
            Directory.CreateDirectory(modelDirectory);
            File.WriteAllText(Path.Combine(modelDirectory, "model.json"), """
                {
                  "id": "NemotronStreamingEn06BInt8_560ms_20260425",
                  "displayName": "Nemotron Streaming English 0.6B",
                  "engine": "online-transducer",
                  "featureDimension": 80,
                  "files": { "encoder": "missing.onnx" }
                }
                """);

            var valid = manager.TryValidateModel(out var model);

            Assert.False(valid);
            Assert.Contains("encoder", model.StatusMessage, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("missing.onnx", model.StatusMessage, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public void CatalogueExposesAConcreteRecommendedModel()
    {
        var root = Path.Combine(Path.GetTempPath(), $"rstt-tests-{Guid.NewGuid():N}");
        try
        {
            var manager = new LocalModelManager(new TestPaths(root), NullLogger<LocalModelManager>.Instance);

            var models = manager.GetAvailableModels();
            var model = Assert.Single(models, candidate => candidate.IsRecommended);

            Assert.Equal(10, models.Count);
            Assert.Equal(LocalModelManager.DefaultModelId, model.Id);
            Assert.True(model.IsRecommended);
            Assert.True(model.DownloadSizeBytes > 600_000_000);
            Assert.Contains("Nemotron", model.DisplayName, StringComparison.OrdinalIgnoreCase);
            Assert.False(model.IsInstalled);
            Assert.Equal(
                3,
                models.Count(candidate =>
                    candidate.Descriptor?.IntegrationStatus ==
                    ModelIntegrationStatus.Available));
            Assert.Equal(
                1,
                models.Count(candidate =>
                    candidate.Descriptor?.IntegrationStatus ==
                    ModelIntegrationStatus.ComingLater));
            Assert.Equal(
                3,
                models.Count(candidate =>
                    candidate.Descriptor?.IntegrationStatus ==
                    ModelIntegrationStatus.Experimental));
            Assert.Equal(
                3,
                models.Count(candidate =>
                    candidate.Descriptor?.IntegrationStatus ==
                    ModelIntegrationStatus.Preview));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public void ManifestCannotEscapeTheModelDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), $"rstt-tests-{Guid.NewGuid():N}");
        try
        {
            var paths = new TestPaths(root);
            paths.EnsureDirectoriesExist();
            var manager = new LocalModelManager(paths, NullLogger<LocalModelManager>.Instance);
            var modelDirectory = manager.GetSelectedModel().Directory;
            Directory.CreateDirectory(modelDirectory);
            File.WriteAllText(Path.Combine(modelDirectory, "model.json"), """
                {
                  "id": "NemotronStreamingEn06BInt8_560ms_20260425",
                  "displayName": "Nemotron Streaming English 0.6B",
                  "engine": "online-transducer",
                  "files": { "encoder": "..\\..\\outside.onnx" }
                }
                """);

            var valid = manager.TryValidateModel(out var model);

            Assert.False(valid);
            Assert.Contains("outside", model.StatusMessage, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    private sealed class TestPaths : IAppPaths
    {
        public TestPaths(string rootDirectory)
        {
            RootDirectory = rootDirectory;
            ModelsDirectory = Path.Combine(rootDirectory, "Models");
            LogsDirectory = Path.Combine(rootDirectory, "Logs");
            SettingsFilePath = Path.Combine(rootDirectory, "settings.json");
        }

        public string RootDirectory { get; }

        public string ModelsDirectory { get; }

        public string LogsDirectory { get; }

        public string SettingsFilePath { get; }

        public void EnsureDirectoriesExist()
        {
            Directory.CreateDirectory(RootDirectory);
            Directory.CreateDirectory(ModelsDirectory);
            Directory.CreateDirectory(LogsDirectory);
        }
    }
}
