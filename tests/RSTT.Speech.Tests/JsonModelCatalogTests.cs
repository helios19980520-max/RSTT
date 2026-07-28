using RSTT.Core.Models;
using RSTT.Speech;
using Xunit;

namespace RSTT.Speech.Tests;

public sealed class JsonModelCatalogTests
{
    [Fact]
    public void EmbeddedCatalogHasValidatedActionableAndRoadmapEntries()
    {
        var catalog = new JsonModelCatalog();
        var models = catalog.GetModels();

        Assert.Empty(catalog.Validate());
        Assert.Equal(10, models.Count);
        Assert.Equal(
            3,
            models.Count(model =>
                model.IntegrationStatus == ModelIntegrationStatus.Available));
        Assert.Equal(
            3,
            models.Count(model =>
                model.IntegrationStatus == ModelIntegrationStatus.Experimental));
        Assert.Equal(
            3,
            models.Count(model =>
                model.IntegrationStatus == ModelIntegrationStatus.Preview));
        Assert.Equal(
            1,
            models.Count(model =>
                model.IntegrationStatus == ModelIntegrationStatus.ComingLater));
        Assert.All(
            models.Where(model =>
                model.IntegrationStatus is
                    ModelIntegrationStatus.Available or ModelIntegrationStatus.Preview),
            model =>
            {
                Assert.NotEmpty(model.Artifacts);
                Assert.All(
                    model.Artifacts,
                    artifact =>
                    {
                        Assert.StartsWith("https://", artifact.SourceUrl);
                        Assert.Equal(64, artifact.Sha256.Length);
                    });
            });
        Assert.All(
            models.Where(model =>
                model.IntegrationStatus is
                    ModelIntegrationStatus.Experimental or ModelIntegrationStatus.ComingLater),
            model => Assert.Empty(model.Artifacts));

        var qwen = catalog.GetById("Qwen3Asr06BInt8");
        Assert.Equal(ModelIntegrationStatus.Preview, qwen.IntegrationStatus);
        Assert.Equal(128, qwen.FeatureDimension);
        Assert.NotEmpty(qwen.Artifacts);
        Assert.Contains(qwen.Artifacts, artifact => artifact.FileName == "tokenizer/vocab.json");
        Assert.Contains("30 languages", qwen.LanguageDescription, StringComparison.Ordinal);
        Assert.Contains("22 Chinese dialects", qwen.LanguageDescription, StringComparison.Ordinal);

        var whisperQ5 = catalog.GetById("WhisperLargeV3TurboQ5_0");
        var whisperFull = catalog.GetById("WhisperLargeV3TurboFull");
        Assert.Equal(ModelIntegrationStatus.Preview, whisperQ5.IntegrationStatus);
        Assert.Equal(574041195, whisperQ5.Artifacts.Single(artifact => artifact.Key == "model").ExpectedBytes);
        Assert.Equal(1624555275, whisperFull.Artifacts.Single(artifact => artifact.Key == "model").ExpectedBytes);
    }
}
