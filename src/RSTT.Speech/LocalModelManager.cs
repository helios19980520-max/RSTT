using System.Text.Json;
using Microsoft.Extensions.Logging;
using RSTT.Core.Abstractions;
using RSTT.Core.Models;

namespace RSTT.Speech;

public sealed partial class LocalModelManager : IModelManager
{
    private const string ManifestFileName = "model.json";
    private static readonly JsonSerializerOptions SerializerOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly IAppPaths _paths;
    private readonly ILogger<LocalModelManager> _logger;

    public LocalModelManager(IAppPaths paths, ILogger<LocalModelManager> logger)
    {
        _paths = paths;
        _logger = logger;
    }

    public ModelInformation GetSelectedModel()
    {
        return TryGetInstallation(out var installation, out var message)
            ? installation.Information
            : new ModelInformation("FastEnglish", "Fast English", GetSelectedDirectory(), false, message);
    }

    public bool TryValidateModel(out ModelInformation model)
    {
        var valid = TryGetInstallation(out var installation, out var message);
        model = valid
            ? installation.Information
            : new ModelInformation("FastEnglish", "Fast English", GetSelectedDirectory(), false, message);
        return valid;
    }

    public ModelInstallation GetSelectedInstallation()
    {
        if (TryGetInstallation(out var installation, out var message))
        {
            return installation;
        }

        throw new InvalidOperationException(message);
    }

    private bool TryGetInstallation(out ModelInstallation installation, out string message)
    {
        _paths.EnsureDirectoriesExist();
        var modelDirectory = GetSelectedDirectory();
        var manifestPath = Path.Combine(modelDirectory, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            installation = default!;
            message = $"No model is installed. Add a model folder containing {ManifestFileName} to {modelDirectory}.";
            return false;
        }

        try
        {
            var manifest = JsonSerializer.Deserialize<ModelManifest>(File.ReadAllText(manifestPath), SerializerOptions);
            if (manifest is null || string.IsNullOrWhiteSpace(manifest.Id) || string.IsNullOrWhiteSpace(manifest.Engine) || manifest.Files.Count == 0)
            {
                installation = default!;
                message = $"The model manifest at {manifestPath} is incomplete.";
                return false;
            }

            var files = manifest.Files.ToDictionary(
                pair => pair.Key,
                pair => Path.GetFullPath(Path.Combine(modelDirectory, pair.Value)),
                StringComparer.OrdinalIgnoreCase);
            var missingFile = files.FirstOrDefault(pair => !File.Exists(pair.Value));
            if (!string.IsNullOrEmpty(missingFile.Key))
            {
                installation = default!;
                message = $"Model file '{missingFile.Key}' is missing: {missingFile.Value}";
                return false;
            }

            var information = new ModelInformation(manifest.Id, manifest.DisplayName, modelDirectory, true, "Model ready");
            installation = new ModelInstallation(information, manifest.Engine, files, Math.Clamp(manifest.NumThreads, 1, 16), manifest.Provider);
            message = "Model ready";
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            LogModelValidationFailed(_logger, exception, modelDirectory);
            installation = default!;
            message = $"The model installation could not be read: {exception.Message}";
            return false;
        }
    }

    private string GetSelectedDirectory() => Path.Combine(_paths.ModelsDirectory, "FastEnglish");

    [LoggerMessage(LogLevel.Warning, "Unable to validate model installation in {ModelDirectory}.")]
    private static partial void LogModelValidationFailed(ILogger logger, Exception exception, string modelDirectory);
}
