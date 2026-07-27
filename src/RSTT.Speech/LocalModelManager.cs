using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RSTT.Core.Abstractions;
using RSTT.Core.Models;

namespace RSTT.Speech;

/// <summary>
/// Owns RSTT's local model catalogue and resumable, verified installation flow.
/// A manifest is written last, so a partial download can never be reported as ready.
/// </summary>
public sealed partial class LocalModelManager : IModelManager, IDisposable
{
    public const string DefaultModelId = "ParakeetUnifiedEnInt8";
    private const string ManifestFileName = "model.json";
    private const string ModelRepository =
        "https://huggingface.co/csukuangfj2/sherpa-onnx-nemo-parakeet-unified-en-0.6b-int8-streaming-1120ms/resolve/main";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private static readonly HttpClient DownloadClient = CreateDownloadClient();
    private static readonly IReadOnlyList<CatalogueModel> Catalogue =
    [
        new(
            DefaultModelId,
            "Parakeet Unified English",
            "parakeet-unified-en-0.6b-int8-streaming-1120ms",
            "Accurate English transcription with punctuation and capitalization, optimized for local CPU inference.",
            "≈ 1.12 s",
            "Highest",
            "online-transducer",
            128,
            true,
            [
                new("encoder", "encoder.int8.onnx", 654_046_391, "1c03f1192de41771384af22972ca10203613ba56197a024f275b86727cd35911"),
                new("decoder", "decoder.int8.onnx", 7_257_777, "34fea72425d2506600772ba191a6d3f99c0710abdb68d9a3dc89fa8cb2aa473a"),
                new("joiner", "joiner.int8.onnx", 1_735_860, "869f43f7d24595c55581ad3bf249a935fb8a71389fbdaa7504b9f46f93140f8a"),
                new("tokens", "tokens.txt", 8_952, "dc0b4584ab2e4ddbf888425c076c61b736e7356a015250db7d307e6f1a8188ff"),
            ]),
    ];

    private readonly IAppPaths _paths;
    private readonly ISettingsService? _settings;
    private readonly ILogger<LocalModelManager> _logger;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly Dictionary<string, ModelAvailability> _transientStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _transientMessages = new(StringComparer.OrdinalIgnoreCase);

    public LocalModelManager(IAppPaths paths, ILogger<LocalModelManager> logger)
        : this(paths, null, logger)
    {
    }

    public LocalModelManager(IAppPaths paths, ISettingsService? settings, ILogger<LocalModelManager> logger)
    {
        _paths = paths;
        _settings = settings;
        _logger = logger;
    }

    public event EventHandler<ModelInformation>? ModelChanged;

    public IReadOnlyList<ModelInformation> GetAvailableModels() =>
        Catalogue.Select(GetModelInformation).ToArray();

    public ModelInformation GetSelectedModel()
    {
        var model = GetSelectedCatalogueModel();
        return GetModelInformation(model);
    }

    public bool TryValidateModel(out ModelInformation model)
    {
        var catalogueModel = GetSelectedCatalogueModel();
        var valid = TryGetInstallation(catalogueModel, out _, out var message);
        model = CreateInformation(
            catalogueModel,
            valid,
            valid ? ModelAvailability.Ready : ModelAvailability.NotInstalled,
            message);
        return valid;
    }

    public ModelInstallation GetSelectedInstallation()
    {
        var catalogueModel = GetSelectedCatalogueModel();
        if (TryGetInstallation(catalogueModel, out var installation, out var message))
        {
            return installation;
        }

        throw new InvalidOperationException(message);
    }

    public async Task SelectAsync(string modelId, CancellationToken cancellationToken = default)
    {
        var model = FindModel(modelId);
        if (_settings is not null)
        {
            _settings.Current.SpeechModel = model.Id;
            await _settings.SaveAsync(cancellationToken).ConfigureAwait(false);
        }

        ModelChanged?.Invoke(this, GetModelInformation(model));
    }

    public async Task DownloadAsync(
        string modelId,
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var model = FindModel(modelId);
        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _paths.EnsureDirectoriesExist();
            SetTransientState(model, ModelAvailability.Downloading, "Preparing download…");
            var directory = GetModelDirectory(model);
            Directory.CreateDirectory(directory);
            var completedBytes = 0L;

            foreach (var file in model.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var targetPath = Path.Combine(directory, file.FileName);
                if (await GetFileValidationErrorAsync(targetPath, file, cancellationToken).ConfigureAwait(false) is null)
                {
                    completedBytes += file.Length;
                    Report(progress, model, ModelAvailability.Downloading, completedBytes, file.FileName, $"Verified {file.FileName}");
                    continue;
                }

                var partialPath = targetPath + ".partial";
                await DownloadFileAsync(model, file, partialPath, completedBytes, progress, cancellationToken).ConfigureAwait(false);
                SetTransientState(model, ModelAvailability.Validating, $"Validating {file.FileName}…");
                Report(progress, model, ModelAvailability.Validating, completedBytes + file.Length, file.FileName, $"Validating {file.FileName}…");

                var validationError = await GetFileValidationErrorAsync(partialPath, file, cancellationToken).ConfigureAwait(false);
                if (validationError is not null)
                {
                    File.Delete(partialPath);
                    throw new InvalidDataException($"{file.FileName} failed validation: {validationError} Download it again.");
                }

                File.Move(partialPath, targetPath, true);
                completedBytes += file.Length;
            }

            SetTransientState(model, ModelAvailability.Installing, "Finishing installation…");
            Report(progress, model, ModelAvailability.Installing, model.TotalBytes, string.Empty, "Writing verified model manifest…");
            await WriteManifestAsync(model, directory, cancellationToken).ConfigureAwait(false);

            if (!TryGetInstallation(model, out _, out var validationMessage))
            {
                throw new InvalidDataException(validationMessage);
            }

            _transientStates.Remove(model.Id);
            _transientMessages.Remove(model.Id);
            if (_settings is not null)
            {
                _settings.Current.SpeechModel = model.Id;
                _settings.Current.HasCompletedOnboarding = true;
                await _settings.SaveAsync(cancellationToken).ConfigureAwait(false);
            }

            var ready = CreateInformation(model, true, ModelAvailability.Ready, "Ready");
            progress?.Report(new ModelDownloadProgress(model.Id, ModelAvailability.Ready, model.TotalBytes, model.TotalBytes, string.Empty, "Model ready"));
            ModelChanged?.Invoke(this, ready);
            LogModelInstalled(_logger, model.Id, directory);
        }
        catch (OperationCanceledException)
        {
            SetTransientState(model, ModelAvailability.NotInstalled, "Download paused. Start again to resume.");
            ModelChanged?.Invoke(this, GetModelInformation(model));
            throw;
        }
        catch (Exception exception)
        {
            SetTransientState(model, ModelAvailability.Error, exception.Message);
            ModelChanged?.Invoke(this, GetModelInformation(model));
            LogModelDownloadFailed(_logger, exception, model.Id);
            throw;
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task DeleteAsync(string modelId, CancellationToken cancellationToken = default)
    {
        var model = FindModel(modelId);
        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = GetModelDirectory(model);
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }

            _transientStates.Remove(model.Id);
            _transientMessages.Remove(model.Id);
            var information = CreateInformation(model, false, ModelAvailability.NotInstalled, "Not installed");
            ModelChanged?.Invoke(this, information);
            LogModelDeleted(_logger, model.Id, directory);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private ModelInformation GetModelInformation(CatalogueModel model)
    {
        if (_transientStates.TryGetValue(model.Id, out var state))
        {
            return CreateInformation(
                model,
                false,
                state,
                _transientMessages.GetValueOrDefault(model.Id) ?? state.ToString());
        }

        var installed = TryGetInstallation(model, out _, out var message);
        return CreateInformation(
            model,
            installed,
            installed ? ModelAvailability.Ready : ModelAvailability.NotInstalled,
            installed ? "Ready" : message);
    }

    private bool TryGetInstallation(CatalogueModel model, out ModelInstallation installation, out string message)
    {
        _paths.EnsureDirectoriesExist();
        var modelDirectory = GetModelDirectory(model);
        var manifestPath = Path.Combine(modelDirectory, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            installation = default!;
            message = $"No verified {ManifestFileName} was found at {manifestPath}.";
            return false;
        }

        try
        {
            var manifest = JsonSerializer.Deserialize<ModelManifest>(File.ReadAllText(manifestPath), SerializerOptions);
            if (manifest is null ||
                !string.Equals(manifest.Id, model.Id, StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(manifest.Engine) ||
                manifest.Files.Count == 0)
            {
                installation = default!;
                message = $"The model manifest at {manifestPath} is incomplete or belongs to a different model.";
                return false;
            }

            var modelRoot = Path.GetFullPath(modelDirectory) + Path.DirectorySeparatorChar;
            var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in manifest.Files)
            {
                var fullPath = Path.GetFullPath(Path.Combine(modelDirectory, pair.Value));
                if (!fullPath.StartsWith(modelRoot, StringComparison.OrdinalIgnoreCase))
                {
                    installation = default!;
                    message = $"Model file '{pair.Key}' resolves outside its installation directory.";
                    return false;
                }

                files[pair.Key] = fullPath;
            }

            foreach (var expected in model.Files)
            {
                if (!files.TryGetValue(expected.Key, out var path) || !File.Exists(path))
                {
                    installation = default!;
                    message = $"Model file '{expected.Key}' is missing: {path ?? expected.FileName}";
                    return false;
                }

                if (new FileInfo(path).Length != expected.Length)
                {
                    installation = default!;
                    message = $"Model file '{expected.Key}' has the wrong size. Reinstall the model.";
                    return false;
                }
            }

            var information = CreateInformation(model, true, ModelAvailability.Ready, "Ready");
            installation = new ModelInstallation(
                information,
                manifest.Engine,
                files,
                Math.Clamp(manifest.NumThreads, 1, 16),
                manifest.Provider,
                Math.Clamp(manifest.FeatureDimension, 40, 256));
            message = "Ready";
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

    private async Task DownloadFileAsync(
        CatalogueModel model,
        CatalogueFile file,
        string partialPath,
        long completedBytes,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var existingLength = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
        if (existingLength > file.Length)
        {
            File.Delete(partialPath);
            existingLength = 0;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ModelRepository}/{file.FileName}");
        // Hugging Face's model CDN has a stable byte-range path. Use it from
        // byte zero too, so fresh and resumed installs follow identical framing.
        request.Headers.Range = new RangeHeaderValue(existingLength, null);

        using var response = await DownloadClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && existingLength == file.Length)
        {
            return;
        }

        response.EnsureSuccessStatusCode();
        var contentRange = response.Content.Headers.ContentRange;
        if (response.StatusCode == HttpStatusCode.PartialContent &&
            (contentRange?.From != existingLength || contentRange.Length != file.Length))
        {
            throw new InvalidDataException(
                $"{file.FileName} returned an unexpected Content-Range. " +
                $"Expected bytes from {existingLength:N0} of {file.Length:N0}.");
        }

        var append = existingLength > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (!append)
        {
            existingLength = 0;
        }

        SetTransientState(model, ModelAvailability.Downloading, $"Downloading {file.FileName}…");
        var mode = append ? FileMode.Append : FileMode.Create;
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var destination = new FileStream(partialPath, mode, FileAccess.Write, FileShare.Read, 128 * 1024, true);
        var buffer = new byte[128 * 1024];
        var fileBytes = existingLength;
        while (true)
        {
            var count = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                break;
            }

            await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            fileBytes += count;
            Report(
                progress,
                model,
                ModelAvailability.Downloading,
                completedBytes + Math.Min(fileBytes, file.Length),
                file.FileName,
                $"Downloading {file.FileName}…");
        }

        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string?> GetFileValidationErrorAsync(
        string path,
        CatalogueFile expected,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return "the downloaded file is missing.";
        }

        var actualLength = new FileInfo(path).Length;
        if (actualLength != expected.Length)
        {
            return $"expected {expected.Length:N0} bytes but received {actualLength:N0} bytes.";
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        var actualHash = Convert.ToHexString(hash);
        return string.Equals(actualHash, expected.Sha256, StringComparison.OrdinalIgnoreCase)
            ? null
            : $"expected SHA-256 {expected.Sha256} but received {actualHash.ToLowerInvariant()}.";
    }

    private static async Task WriteManifestAsync(CatalogueModel model, string directory, CancellationToken cancellationToken)
    {
        var manifest = new ModelManifest
        {
            Id = model.Id,
            DisplayName = model.DisplayName,
            Engine = model.Engine,
            FeatureDimension = model.FeatureDimension,
            Files = model.Files.ToDictionary(file => file.Key, file => file.FileName, StringComparer.OrdinalIgnoreCase),
            FileSizes = model.Files.ToDictionary(file => file.Key, file => file.Length, StringComparer.OrdinalIgnoreCase),
            Sha256 = model.Files.ToDictionary(file => file.Key, file => file.Sha256, StringComparer.OrdinalIgnoreCase),
            NumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4),
            Provider = "cpu",
        };
        var manifestPath = Path.Combine(directory, ManifestFileName);
        var temporaryPath = manifestPath + ".tmp";
        await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 16 * 1024, true))
        {
            await JsonSerializer.SerializeAsync(stream, manifest, SerializerOptions, cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporaryPath, manifestPath, true);
    }

    private ModelInformation CreateInformation(
        CatalogueModel model,
        bool isInstalled,
        ModelAvailability availability,
        string message) =>
        new(
            model.Id,
            model.DisplayName,
            GetModelDirectory(model),
            isInstalled,
            message,
            availability,
            model.TotalBytes,
            model.Description,
            model.Latency,
            model.Accuracy,
            model.IsRecommended);

    private CatalogueModel GetSelectedCatalogueModel()
    {
        var id = _settings?.Current.SpeechModel;
        return Catalogue.FirstOrDefault(model => string.Equals(model.Id, id, StringComparison.OrdinalIgnoreCase))
            ?? Catalogue[0];
    }

    private static CatalogueModel FindModel(string modelId) =>
        Catalogue.FirstOrDefault(model => string.Equals(model.Id, modelId, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentOutOfRangeException(nameof(modelId), modelId, "The requested model is not in the RSTT catalogue.");

    private string GetModelDirectory(CatalogueModel model) =>
        Path.Combine(_paths.ModelsDirectory, model.DirectoryName);

    private void SetTransientState(CatalogueModel model, ModelAvailability state, string message)
    {
        _transientStates[model.Id] = state;
        _transientMessages[model.Id] = message;
        ModelChanged?.Invoke(this, CreateInformation(model, false, state, message));
    }

    private static void Report(
        IProgress<ModelDownloadProgress>? progress,
        CatalogueModel model,
        ModelAvailability state,
        long received,
        string currentFile,
        string message) =>
        progress?.Report(new ModelDownloadProgress(model.Id, state, received, model.TotalBytes, currentFile, message));

    private static HttpClient CreateDownloadClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("RSTT/1.0 (+https://github.com/helios/rstt)");
        return client;
    }

    public void Dispose() => _operationLock.Dispose();

    private sealed record CatalogueModel(
        string Id,
        string DisplayName,
        string DirectoryName,
        string Description,
        string Latency,
        string Accuracy,
        string Engine,
        int FeatureDimension,
        bool IsRecommended,
        IReadOnlyList<CatalogueFile> Files)
    {
        public long TotalBytes => Files.Sum(file => file.Length);
    }

    private sealed record CatalogueFile(string Key, string FileName, long Length, string Sha256);

    [LoggerMessage(LogLevel.Information, "Installed and verified model {ModelId} at {ModelDirectory}.")]
    private static partial void LogModelInstalled(ILogger logger, string modelId, string modelDirectory);

    [LoggerMessage(LogLevel.Information, "Deleted model {ModelId} from {ModelDirectory}.")]
    private static partial void LogModelDeleted(ILogger logger, string modelId, string modelDirectory);

    [LoggerMessage(LogLevel.Warning, "Unable to validate model installation in {ModelDirectory}.")]
    private static partial void LogModelValidationFailed(ILogger logger, Exception exception, string modelDirectory);

    [LoggerMessage(LogLevel.Error, "Model {ModelId} could not be downloaded or installed.")]
    private static partial void LogModelDownloadFailed(ILogger logger, Exception exception, string modelId);
}
