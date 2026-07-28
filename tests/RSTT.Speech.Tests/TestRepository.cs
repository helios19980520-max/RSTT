namespace RSTT.Speech.Tests;

internal static class TestRepository
{
    public static string AppWorkerRoot
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null &&
                   !File.Exists(Path.Combine(directory.FullName, "RSTT.sln")))
            {
                directory = directory.Parent;
            }

            if (directory is null)
            {
                throw new DirectoryNotFoundException(
                    "Could not locate the RSTT repository root.");
            }

#if DEBUG
            const string configuration = "Debug";
#else
            const string configuration = "Release";
#endif
            return Path.Combine(
                directory.FullName,
                "src",
                "RSTT.App",
                "bin",
                configuration,
                "net8.0-windows",
                "win-x64");
        }
    }
}
