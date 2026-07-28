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
        Assert.Equal(8, models.Count);
        Assert.Equal(
            3,
            models.Count(model =>
                model.IntegrationStatus == ModelIntegrationStatus.Available));
        Assert.Equal(
            4,
            models.Count(model =>
                model.IntegrationStatus == ModelIntegrationStatus.Experimental));
        Assert.Equal(
            1,
            models.Count(model =>
                model.IntegrationStatus == ModelIntegrationStatus.ComingLater));
        Assert.All(
            models.Where(model =>
                model.IntegrationStatus == ModelIntegrationStatus.Available),
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
                model.IntegrationStatus != ModelIntegrationStatus.Available),
            model => Assert.Empty(model.Artifacts));

        var qwen = catalog.GetById("Qwen3Asr06BInt8");
        Assert.Contains("30 languages", qwen.LanguageDescription, StringComparison.Ordinal);
        Assert.Contains("22 Chinese dialects", qwen.LanguageDescription, StringComparison.Ordinal);
    }
}
