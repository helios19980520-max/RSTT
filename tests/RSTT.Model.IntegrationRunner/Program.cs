using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RSTT.Core.Abstractions;
using RSTT.Core.Models;
using RSTT.Speech;

return await RunAsync(args).ConfigureAwait(false);

static async Task<int> RunAsync(string[] args)
{
    try
    {
        var modelId = RequiredArgument(args, "--model");
        var root = OptionalArgument(args, "--root") ??
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Helios",
                "RSTT");
        var paths = new RunnerAppPaths(Path.GetFullPath(root));
        var catalog = new JsonModelCatalog();
        using var manager = new LocalModelManager(
            paths,
            null,
            catalog,
            NullLogger<LocalModelManager>.Instance);
        var lastPercent = -1;
        var progress = new Progress<ModelDownloadProgress>(item =>
        {
            var percent = item.TotalBytes <= 0
                ? 0
                : (int)Math.Floor(item.BytesReceived * 100d / item.TotalBytes);
            if (percent == lastPercent)
            {
                return;
            }

            lastPercent = percent;
            Console.Error.WriteLine(
                $"{percent,3}% {item.BytesReceived}/{item.TotalBytes} bytes " +
                $"{item.BytesPerSecond / 1024d / 1024d:N1} MiB/s " +
                $"ETA {item.Eta:g}");
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromHours(2));
        await manager.DownloadAsync(modelId, progress, timeout.Token).ConfigureAwait(false);
        await manager.SelectAsync(modelId, timeout.Token).ConfigureAwait(false);
        var installation = manager.GetSelectedInstallation();
        Console.WriteLine(JsonSerializer.Serialize(
            new
            {
                installation.Information.Id,
                installation.Information.DisplayName,
                installation.Information.Directory,
                installation.Engine,
                installation.FeatureDimension,
                Files = installation.Files.Count,
                installation.Information.IsInstalled,
                installation.Information.StatusMessage,
            },
            new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine(exception);
        return 1;
    }
}

static string RequiredArgument(string[] args, string name) =>
    OptionalArgument(args, name) ??
    throw new ArgumentException($"Required argument is missing: {name}");

static string? OptionalArgument(string[] args, string name)
{
    for (var index = 0; index < args.Length - 1; index++)
    {
        if (args[index].Equals(name, StringComparison.OrdinalIgnoreCase))
        {
            return args[index + 1];
        }
    }

    return null;
}

internal sealed class RunnerAppPaths(string rootDirectory) : IAppPaths
{
    public string RootDirectory { get; } = rootDirectory;

    public string ModelsDirectory => Path.Combine(RootDirectory, "Models");

    public string LogsDirectory => Path.Combine(RootDirectory, "Logs");

    public string SettingsFilePath => Path.Combine(RootDirectory, "settings.json");

    public void EnsureDirectoriesExist()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(ModelsDirectory);
        Directory.CreateDirectory(LogsDirectory);
    }
}
