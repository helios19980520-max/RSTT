using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RSTT.Core.Abstractions;
using RSTT.Infrastructure;
using RSTT.Speech;
using Xunit;

namespace RSTT.Speech.Tests;

public sealed class JsonSettingsServiceTests
{
    [Fact]
    public async Task ConcurrentSavesRemainAtomicAndReadable()
    {
        var root = Path.Combine(Path.GetTempPath(), $"rstt-settings-{Guid.NewGuid():N}");
        try
        {
            var paths = new TestPaths(root);
            using var service = new JsonSettingsService(paths, NullLogger<JsonSettingsService>.Instance);
            service.Current.CaptionFontSize = 31;
            service.Current.AudioDeviceId = "device-1";

            await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => service.SaveAsync()));

            await using var stream = File.OpenRead(paths.SettingsFilePath);
            using var json = await JsonDocument.ParseAsync(stream);
            Assert.Equal(31, json.RootElement.GetProperty("CaptionFontSize").GetDouble());
            Assert.Equal("device-1", json.RootElement.GetProperty("AudioDeviceId").GetString());
            Assert.False(File.Exists(paths.SettingsFilePath + ".tmp"));
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
    public async Task MalformedSettingsFallBackToSafeDefaults()
    {
        var root = Path.Combine(Path.GetTempPath(), $"rstt-settings-{Guid.NewGuid():N}");
        try
        {
            var paths = new TestPaths(root);
            paths.EnsureDirectoriesExist();
            await File.WriteAllTextAsync(paths.SettingsFilePath, "{ not-json");
            using var service = new JsonSettingsService(paths, NullLogger<JsonSettingsService>.Instance);

            await service.LoadAsync();

            Assert.Equal(LocalModelManager.DefaultModelId, service.Current.SpeechModel);
            Assert.True(service.Current.TextInjectionEnabled);
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
        public TestPaths(string root)
        {
            RootDirectory = root;
            ModelsDirectory = Path.Combine(root, "Models");
            LogsDirectory = Path.Combine(root, "Logs");
            SettingsFilePath = Path.Combine(root, "settings.json");
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
