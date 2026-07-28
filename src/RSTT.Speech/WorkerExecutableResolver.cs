namespace RSTT.Speech;

internal static class WorkerExecutableResolver
{
    public static string Resolve(
        string applicationBaseDirectory,
        string relativeWorkerDirectory,
        string workerName,
        string developmentProject,
        bool allowDevelopmentFallback)
    {
        var local = Path.Combine(
            Path.GetFullPath(applicationBaseDirectory),
            relativeWorkerDirectory,
            workerName);
        if (File.Exists(local) || !allowDevelopmentFallback)
        {
            return local;
        }

        var repository = FindRepositoryRoot(applicationBaseDirectory);
        if (repository is null)
        {
            return local;
        }

        return Path.Combine(
            repository,
            "src",
            developmentProject,
            "bin",
            "Debug",
            "net8.0-windows",
            "win-x64",
            workerName);
    }

    private static string? FindRepositoryRoot(string start)
    {
        var directory = new DirectoryInfo(start);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "RSTT.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
