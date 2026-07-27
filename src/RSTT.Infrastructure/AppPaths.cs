using RSTT.Core.Abstractions;

namespace RSTT.Infrastructure;

public sealed class AppPaths : IAppPaths
{
    public AppPaths()
    {
        RootDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Helios", "RSTT");
        ModelsDirectory = Path.Combine(RootDirectory, "Models");
        LogsDirectory = Path.Combine(RootDirectory, "Logs");
        SettingsFilePath = Path.Combine(RootDirectory, "settings.json");
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
