namespace RSTT.Core.Abstractions;

public interface IAppPaths
{
    string RootDirectory { get; }

    string ModelsDirectory { get; }

    string LogsDirectory { get; }

    string SettingsFilePath { get; }

    void EnsureDirectoriesExist();
}
