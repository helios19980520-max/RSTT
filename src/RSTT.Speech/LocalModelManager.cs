using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RSTT.Core.Abstractions;
using RSTT.Core.Models;

namespace RSTT.Speech;

/// <summary>
/// Curated model library with resumable staging, exact validation, and same-volume
/// directory promotion. A model is never visible as installed until its manifest is
/// written last and the completed staging directory is promoted.
/// </summary>
public sealed partial class LocalModelManager : IModelManager, IDisposable
{
    public const string DefaultModelId = "NemotronStreamingEn06BInt8_560ms_20260425";
    private const string ManifestFileName = "model.json";
    private const string DownloadsDirectoryName = ".downloads";
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(75);
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };
    private static readonly HttpClient DownloadClient = CreateDownloadClient();

    private readonly IAppPaths _paths;
    private readonly ISettingsService? _settings;
    private readonly IModelCatalog _catalog;
    private readonly ILogger<LocalModelManager> _logger;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly object _stateGate = new();
    private readonly Dictionary<string, ModelAvailability> _transientStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _transientMessages = new(StringComparer.OrdinalIgnoreCase);
    private string? _activeModelId;

    public LocalModelManager(IAppPaths paths, ILogger<LocalModelManager> logger)
        : this(paths, null, new JsonModelCatalog(), logger)
    {
    }

    public LocalModelManager(
        IAppPaths paths,
        ISettingsService? settings,
        ILogger<LocalModelManager> logger)
        : this(paths, settings, new JsonModelCatalog(), logger)
    {
    }

    public LocalModelManager(
        IAppPaths paths,
        ISettingsService? settings,
        IModelCatalog catalog,
        ILogger<LocalModelManager> logger)
    {
        _paths = paths;
        _settings = settings;
        _catalog = catalog;
        _logger = logger;
    }

    public event EventHandler<ModelInformation>? ModelChanged;

    public IReadOnlyList<ModelInformation> GetAvailableModels() =>
        _catalog.GetModels()
            .OrderByDescending(model => model.IsRecommended)
            .ThenBy(model => model.IntegrationStatus)
            .ThenBy(model => model.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Select(GetModelInformation)
            .ToArray();

    public ModelInformation GetSelectedModel()
    {
        var model = GetSelectedDescriptor();
        return GetModelInformation(model);
    }

    public bool TryValidateModel(out ModelInformation model)
    {
        var descriptor = GetSelectedDescriptor();
        var valid = TryGetInstallation(descriptor, out _, out var message);
        model = CreateInformation(
            descriptor,
            valid,
            valid ? ModelAvailability.Ready : ModelAvailability.NotInstalled,
            message);
        return valid;
    }

    public ModelInstallation GetSelectedInstallation()
    {
        var model = GetSelectedDescriptor();
        if (TryGetInstallation(model, out var installation, out var message))
        {
            return installation;
        }

        throw new InvalidOperationException(message);
    }

    public Task SelectAsync(string modelId, CancellationToken cancellationToken = default)
    {
        var model = _catalog.GetById(modelId);
        if (!IsActionable(model))
        {
            throw new InvalidOperationException($"{model.DisplayName} is {FormatIntegrationStatus(model.IntegrationStatus)} and cannot be activated.");
        }

        if (!TryGetInstallation(model, out _, out var message))
        {
            throw new InvalidOperationException($"{model.DisplayName} is not ready: {message}");
        }

        cancellationToken.ThrowIfCancellationRequested();
        _activeModelId = model.Id;
        ModelChanged?.Invoke(this, GetModelInformation(model));
        return Task.CompletedTask;
    }

    public async Task SetDefaultAsync(
        string modelId,
        CancellationToken cancellationToken = default)
    {
        var model = _catalog.GetById(modelId);
        if (!IsActionable(model))
        {
            throw new InvalidOperationException(
                $"{model.DisplayName} cannot be the default because it is {FormatIntegrationStatus(model.IntegrationStatus)}.");
        }

        if (!TryGetInstallation(model, out _, out var message))
        {
            throw new InvalidOperationException(
                $"{model.DisplayName} cannot be the default: {message}");
        }

        if (_settings is null)
        {
            return;
        }

        _settings.Current.DefaultModelId = model.Id;
        _settings.Current.DefaultProfileId = model.LatencyProfiles
            .FirstOrDefault(profile =>
                profile.Id.Contains("balanced", StringComparison.OrdinalIgnoreCase))?.Id
            ?? (model.LatencyProfiles.Count > 0
                ? model.LatencyProfiles[0].Id
                : null)
            ?? string.Empty;
        _settings.Current.SpeechModel = model.Id;
        await _settings.SaveAsync(cancellationToken).ConfigureAwait(false);
        ModelChanged?.Invoke(this, GetModelInformation(model));
    }

    public async Task DownloadAsync(
        string modelId,
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var model = _catalog.GetById(modelId);
        if (!IsActionable(model))
        {
            throw new InvalidOperationException($"{model.DisplayName} is not downloadable because its integration is {FormatIntegrationStatus(model.IntegrationStatus)}.");
        }

        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _paths.EnsureDirectoriesExist();
            if (TryGetInstallation(model, out _, out _))
            {
                ReportReady(progress, model);
                return;
            }

            EnsureDiskSpace(model);
            SetTransientState(model, ModelAvailability.Downloading, "Preparing secure download…");
            var stagingDirectory = GetStagingDirectory(model);
            Directory.CreateDirectory(stagingDirectory);
            var totalBytes = model.Artifacts.Sum(artifact => artifact.ExpectedBytes);
            var completedBytes = 0L;
            var sessionStopwatch = Stopwatch.StartNew();
            var totalFiles = model.Artifacts.Count;

            for (var fileIndex = 0; fileIndex < model.Artifacts.Count; fileIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var artifact = model.Artifacts[fileIndex];
                var stagedPath = Path.Combine(stagingDirectory, artifact.FileName);
                var stagedParent = Path.GetDirectoryName(stagedPath);
                if (!string.IsNullOrWhiteSpace(stagedParent))
                {
                    Directory.CreateDirectory(stagedParent);
                }

                if (await GetFileValidationErrorAsync(stagedPath, artifact, cancellationToken).ConfigureAwait(false) is null)
                {
                    completedBytes += artifact.ExpectedBytes;
                    Report(
                        progress,
                        model,
                        ModelAvailability.Downloading,
                        completedBytes,
                        totalBytes,
                        fileIndex + 1,
                        totalFiles,
                        artifact.FileName,
                        artifact.ExpectedBytes,
                        artifact.ExpectedBytes,
                        sessionStopwatch,
                        $"Verified {artifact.FileName}");
                    continue;
                }

                var partialPath = stagedPath + ".partial";
                await DownloadFileAsync(
                    model,
                    artifact,
                    partialPath,
                    completedBytes,
                    totalBytes,
                    fileIndex,
                    totalFiles,
                    sessionStopwatch,
                    progress,
                    cancellationToken).ConfigureAwait(false);

                SetTransientState(model, ModelAvailability.Validating, $"Validating {artifact.FileName}…");
                var validationError = await GetFileValidationErrorAsync(partialPath, artifact, cancellationToken).ConfigureAwait(false);
                if (validationError is not null)
                {
                    File.Delete(partialPath);
                    throw new InvalidDataException(
                        $"{artifact.FileName} failed validation: {validationError} The corrupted partial file was removed.");
                }

                File.Move(partialPath, stagedPath, true);
                completedBytes += artifact.ExpectedBytes;
                Report(
                    progress,
                    model,
                    ModelAvailability.Validating,
                    completedBytes,
                    totalBytes,
                    fileIndex + 1,
                    totalFiles,
                    artifact.FileName,
                    artifact.ExpectedBytes,
                    artifact.ExpectedBytes,
                    sessionStopwatch,
                    $"Validated {artifact.FileName}");
            }

            SetTransientState(model, ModelAvailability.Installing, "Promoting verified model…");
            await WriteManifestAsync(model, stagingDirectory, cancellationToken).ConfigureAwait(false);
            PromoteStagingDirectory(model, stagingDirectory);
            if (!TryGetInstallation(model, out _, out var validationMessage))
            {
                throw new InvalidDataException(validationMessage);
            }

            lock (_stateGate)
            {
                _transientStates.Remove(model.Id);
                _transientMessages.Remove(model.Id);
            }

            if (_settings is not null)
            {
                _settings.Current.HasCompletedOnboarding = true;
                await _settings.SaveAsync(cancellationToken).ConfigureAwait(false);
            }

            ReportReady(progress, model);
            var ready = CreateInformation(model, true, ModelAvailability.Ready, "Ready");
            ModelChanged?.Invoke(this, ready);
            LogModelInstalled(_logger, model.Id, GetModelDirectory(model));
        }
        catch (OperationCanceledException)
        {
            SetTransientState(model, ModelAvailability.NotInstalled, "Download paused. Start again to resume.");
            throw;
        }
        catch (Exception exception)
        {
            SetTransientState(model, ModelAvailability.Error, exception.Message);
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
        var model = _catalog.GetById(modelId);
        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            DeleteDirectoryIfPresent(GetModelDirectory(model));
            DeleteDirectoryIfPresent(GetStagingDirectory(model));
            lock (_stateGate)
            {
                _transientStates.Remove(model.Id);
                _transientMessages.Remove(model.Id);
            }

            var information = CreateInformation(model, false, ModelAvailability.NotInstalled, "Not installed");
            ModelChanged?.Invoke(this, information);
            LogModelDeleted(_logger, model.Id, GetModelDirectory(model));
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private ModelInformation GetModelInformation(ModelDescriptor model)
    {
        lock (_stateGate)
        {
            if (_transientStates.TryGetValue(model.Id, out var state))
            {
                return CreateInformation(
                    model,
                    false,
                    state,
                    _transientMessages.GetValueOrDefault(model.Id) ?? state.ToString());
            }
        }

        if (!IsActionable(model))
        {
            return CreateInformation(
                model,
                false,
                ModelAvailability.NotInstalled,
                FormatIntegrationStatus(model.IntegrationStatus));
        }

        var installed = TryGetInstallation(model, out _, out var message);
        return CreateInformation(
            model,
            installed,
            installed ? ModelAvailability.Ready : ModelAvailability.NotInstalled,
            installed ? "Ready" : message);
    }

    private bool TryGetInstallation(
        ModelDescriptor model,
        out ModelInstallation installation,
        out string message)
    {
        if (!IsActionable(model))
        {
            installation = default!;
            message = FormatIntegrationStatus(model.IntegrationStatus);
            return false;
        }

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
            var manifest = JsonSerializer.Deserialize<ModelManifest>(
                File.ReadAllText(manifestPath),
                SerializerOptions);
            if (manifest is null ||
                !string.Equals(manifest.Id, model.Id, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(manifest.Engine, model.Engine, StringComparison.OrdinalIgnoreCase) ||
                manifest.Files.Count == 0)
            {
                installation = default!;
                message = $"The model manifest at {manifestPath} is incomplete or belongs to a different model.";
                return false;
            }

            if (!string.IsNullOrWhiteSpace(manifest.Revision) &&
                !string.Equals(manifest.Revision, model.Revision, StringComparison.Ordinal))
            {
                installation = default!;
                message = $"The installed model revision '{manifest.Revision}' does not match catalog revision '{model.Revision}'.";
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

            foreach (var expected in model.Artifacts.Where(artifact => artifact.IsRequired))
            {
                if (!files.TryGetValue(expected.Key, out var path) || !File.Exists(path))
                {
                    installation = default!;
                    message = $"Model file '{expected.Key}' is missing: {path ?? expected.FileName}";
                    return false;
                }

                if (new FileInfo(path).Length != expected.ExpectedBytes)
                {
                    installation = default!;
                    message = $"Model file '{expected.Key}' has the wrong size. Reinstall the model.";
                    return false;
                }

                if (manifest.Sha256.TryGetValue(expected.Key, out var declaredHash) &&
                    !string.Equals(declaredHash, expected.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    installation = default!;
                    message = $"Model file '{expected.Key}' was installed from an unrecognized revision.";
                    return false;
                }
            }

            if (string.Equals(model.Engine, "offline-qwen3-asr", StringComparison.OrdinalIgnoreCase))
            {
                var tokenizerDirectory = Path.GetFullPath(Path.Combine(modelDirectory, "tokenizer"));
                if (!tokenizerDirectory.StartsWith(modelRoot, StringComparison.OrdinalIgnoreCase) ||
                    !Directory.Exists(tokenizerDirectory))
                {
                    installation = default!;
                    message = "The Qwen tokenizer directory is missing or unsafe.";
                    return false;
                }

                files["tokenizer"] = tokenizerDirectory;
            }

            var profile = model.LatencyProfiles.FirstOrDefault(profile =>
                    string.Equals(profile.Id, manifest.RecognitionProfileId, StringComparison.OrdinalIgnoreCase))
                ?? (model.LatencyProfiles.Count > 0 ? model.LatencyProfiles[0] : null);
            var recommendedThreads = profile?.RecommendedThreads ?? 2;
            var threads = Math.Clamp(Math.Min(manifest.NumThreads, recommendedThreads), 1, 4);
            var information = CreateInformation(model, true, ModelAvailability.Ready, "Ready");
            installation = new ModelInstallation(
                information,
                manifest.Engine,
                files,
                threads,
                manifest.Provider,
                Math.Clamp(manifest.FeatureDimension, 40, 256),
                profile,
                model);
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
        ModelDescriptor model,
        ModelArtifact artifact,
        string partialPath,
        long completedBytes,
        long totalBytes,
        int fileIndex,
        int totalFiles,
        Stopwatch sessionStopwatch,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var existingLength = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
        if (existingLength > artifact.ExpectedBytes)
        {
            File.Delete(partialPath);
            existingLength = 0;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, artifact.SourceUrl);
        request.Headers.Range = new RangeHeaderValue(existingLength, null);
        using var response = await DownloadClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable &&
            existingLength == artifact.ExpectedBytes)
        {
            return;
        }

        response.EnsureSuccessStatusCode();
        var contentRange = response.Content.Headers.ContentRange;
        if (response.StatusCode == HttpStatusCode.PartialContent &&
            (contentRange?.From != existingLength || contentRange.Length != artifact.ExpectedBytes))
        {
            throw new InvalidDataException(
                $"{artifact.FileName} returned an unexpected Content-Range. " +
                $"Expected bytes from {existingLength:N0} of {artifact.ExpectedBytes:N0}.");
        }

        var append = existingLength > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (!append)
        {
            existingLength = 0;
        }

        SetTransientState(model, ModelAvailability.Downloading, $"Downloading {artifact.FileName}…");
        var mode = append ? FileMode.Append : FileMode.Create;
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var destination = new FileStream(
            partialPath,
            mode,
            FileAccess.Write,
            FileShare.Read,
            256 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = ArrayPool<byte>.Shared.Rent(256 * 1024);
        try
        {
            var fileBytes = existingLength;
            var lastReportAt = TimeSpan.Zero;
            while (true)
            {
                var count = await source.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (count == 0)
                {
                    break;
                }

                await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                fileBytes += count;
                if (sessionStopwatch.Elapsed - lastReportAt >= ProgressInterval)
                {
                    lastReportAt = sessionStopwatch.Elapsed;
                    Report(
                        progress,
                        model,
                        ModelAvailability.Downloading,
                        completedBytes + Math.Min(fileBytes, artifact.ExpectedBytes),
                        totalBytes,
                        fileIndex + 1,
                        totalFiles,
                        artifact.FileName,
                        fileBytes,
                        artifact.ExpectedBytes,
                        sessionStopwatch,
                        $"Downloading {artifact.FileName}…");
                }
            }

            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            Report(
                progress,
                model,
                ModelAvailability.Downloading,
                completedBytes + Math.Min(fileBytes, artifact.ExpectedBytes),
                totalBytes,
                fileIndex + 1,
                totalFiles,
                artifact.FileName,
                fileBytes,
                artifact.ExpectedBytes,
                sessionStopwatch,
                $"Downloaded {artifact.FileName}");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task<string?> GetFileValidationErrorAsync(
        string path,
        ModelArtifact expected,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return "the downloaded file is missing.";
        }

        var actualLength = new FileInfo(path).Length;
        if (actualLength != expected.ExpectedBytes)
        {
            return $"expected {expected.ExpectedBytes:N0} bytes but received {actualLength:N0} bytes.";
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            256 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        var actualHash = Convert.ToHexString(hash);
        return string.Equals(actualHash, expected.Sha256, StringComparison.OrdinalIgnoreCase)
            ? null
            : $"expected SHA-256 {expected.Sha256} but received {actualHash.ToLowerInvariant()}.";
    }

    private static async Task WriteManifestAsync(
        ModelDescriptor model,
        string directory,
        CancellationToken cancellationToken)
    {
        var profile = model.LatencyProfiles.Count > 0 ? model.LatencyProfiles[0] : null;
        var manifest = new ModelManifest
        {
            Id = model.Id,
            DisplayName = model.DisplayName,
            Engine = model.Engine,
            Revision = model.Revision,
            FeatureDimension = model.FeatureDimension,
            RecognitionProfileId = profile?.Id ?? string.Empty,
            Files = model.Artifacts.ToDictionary(
                artifact => artifact.Key,
                artifact => artifact.FileName,
                StringComparer.OrdinalIgnoreCase),
            FileSizes = model.Artifacts.ToDictionary(
                artifact => artifact.Key,
                artifact => artifact.ExpectedBytes,
                StringComparer.OrdinalIgnoreCase),
            Sha256 = model.Artifacts.ToDictionary(
                artifact => artifact.Key,
                artifact => artifact.Sha256,
                StringComparer.OrdinalIgnoreCase),
            NumThreads = profile?.RecommendedThreads ?? 2,
            Provider = "cpu",
        };
        var manifestPath = Path.Combine(directory, ManifestFileName);
        var temporaryPath = manifestPath + ".tmp";
        await using (var stream = new FileStream(
                         temporaryPath,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.None,
                         16 * 1024,
                         FileOptions.Asynchronous))
        {
            await JsonSerializer.SerializeAsync(
                stream,
                manifest,
                SerializerOptions,
                cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporaryPath, manifestPath, true);
    }

    private ModelDescriptor GetSelectedDescriptor()
    {
        var models = _catalog.GetModels();
        var requested = models.FirstOrDefault(model =>
            string.Equals(model.Id, GetConfiguredModelId(), StringComparison.OrdinalIgnoreCase));
        if (requested is not null &&
            IsActionable(requested) &&
            TryGetInstallation(requested, out _, out _))
        {
            return requested;
        }

        var installed = models
            .Where(IsActionable)
            .OrderByDescending(model => model.IsRecommended)
            .FirstOrDefault(model => TryGetInstallation(model, out _, out _));
        return installed
            ?? requested
            ?? models.First(model => string.Equals(model.Id, DefaultModelId, StringComparison.OrdinalIgnoreCase));
    }

    private ModelInformation CreateInformation(
        ModelDescriptor model,
        bool isInstalled,
        ModelAvailability availability,
        string message)
    {
        var profile = model.LatencyProfiles.Count > 0 ? model.LatencyProfiles[0] : null;
        var active = string.Equals(
            GetConfiguredModelId(),
            model.Id,
            StringComparison.OrdinalIgnoreCase);
        return new ModelInformation(
            model.Id,
            model.DisplayName,
            string.IsNullOrWhiteSpace(model.DirectoryName) ? string.Empty : GetModelDirectory(model),
            isInstalled,
            message,
            availability,
            model.DownloadBytes,
            model.Description,
            profile is null ? "—" : $"~{profile.ExpectedLatencyMs:N0} ms · {profile.DisplayName}",
            SplitPascalCase(model.AccuracyTier.ToString()),
            model.IsRecommended,
            isInstalled ? model.InstalledBytes : 0,
            active,
            model);
    }

    private string GetConfiguredModelId() =>
        !string.IsNullOrWhiteSpace(_activeModelId)
            ? _activeModelId
            : string.IsNullOrWhiteSpace(_settings?.Current.DefaultModelId)
            ? DefaultModelId
            : _settings.Current.DefaultModelId;

    private string GetModelDirectory(ModelDescriptor model) =>
        Path.Combine(_paths.ModelsDirectory, model.DirectoryName);

    private string GetStagingDirectory(ModelDescriptor model) =>
        Path.Combine(_paths.ModelsDirectory, DownloadsDirectoryName, model.DirectoryName);

    private void SetTransientState(ModelDescriptor model, ModelAvailability state, string message)
    {
        lock (_stateGate)
        {
            _transientStates[model.Id] = state;
            _transientMessages[model.Id] = message;
        }

        ModelChanged?.Invoke(this, CreateInformation(model, false, state, message));
    }

    private static void Report(
        IProgress<ModelDownloadProgress>? progress,
        ModelDescriptor model,
        ModelAvailability state,
        long received,
        long totalBytes,
        int currentFileIndex,
        int totalFiles,
        string currentFile,
        long currentFileBytes,
        long currentFileTotalBytes,
        Stopwatch stopwatch,
        string message)
    {
        if (progress is null)
        {
            return;
        }

        var elapsedSeconds = Math.Max(0.001, stopwatch.Elapsed.TotalSeconds);
        var bytesPerSecond = received / elapsedSeconds;
        var remainingBytes = Math.Max(0, totalBytes - received);
        TimeSpan? eta = bytesPerSecond <= 0
            ? null
            : TimeSpan.FromSeconds(remainingBytes / bytesPerSecond);
        progress.Report(new ModelDownloadProgress(
            model.Id,
            state,
            received,
            totalBytes,
            currentFile,
            message,
            currentFileIndex,
            totalFiles,
            currentFileBytes,
            currentFileTotalBytes,
            bytesPerSecond,
            eta));
    }

    private static void ReportReady(IProgress<ModelDownloadProgress>? progress, ModelDescriptor model) =>
        progress?.Report(new ModelDownloadProgress(
            model.Id,
            ModelAvailability.Ready,
            model.DownloadBytes,
            model.DownloadBytes,
            string.Empty,
            "Model ready",
            model.Artifacts.Count,
            model.Artifacts.Count,
            0,
            0));

    private void EnsureDiskSpace(ModelDescriptor model)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(_paths.ModelsDirectory));
        if (string.IsNullOrWhiteSpace(root))
        {
            return;
        }

        var drive = new DriveInfo(root);
        var safetyReserve = 256L * 1024 * 1024;
        var required = model.DownloadBytes + safetyReserve;
        if (drive.AvailableFreeSpace < required)
        {
            throw new IOException(
                $"Not enough disk space to install {model.DisplayName}. " +
                $"RSTT needs at least {required / 1024d / 1024d:N0} MB free.");
        }
    }

    private void PromoteStagingDirectory(ModelDescriptor model, string stagingDirectory)
    {
        var targetDirectory = GetModelDirectory(model);
        var backupDirectory = targetDirectory + $".previous-{Guid.NewGuid():N}";
        var hadExisting = Directory.Exists(targetDirectory);
        try
        {
            if (hadExisting)
            {
                Directory.Move(targetDirectory, backupDirectory);
            }

            Directory.Move(stagingDirectory, targetDirectory);
            DeleteDirectoryIfPresent(backupDirectory);
        }
        catch
        {
            if (!Directory.Exists(targetDirectory) && Directory.Exists(backupDirectory))
            {
                Directory.Move(backupDirectory, targetDirectory);
            }

            throw;
        }
    }

    private static void DeleteDirectoryIfPresent(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }

    private static string FormatIntegrationStatus(ModelIntegrationStatus status) =>
        status switch
        {
            ModelIntegrationStatus.Preview => "Preview",
            ModelIntegrationStatus.Experimental => "Experimental",
            ModelIntegrationStatus.ComingLater => "Coming later",
            _ => "Available",
        };

    private static bool IsActionable(ModelDescriptor model) =>
        model.IntegrationStatus is ModelIntegrationStatus.Available or ModelIntegrationStatus.Preview;

    private static string SplitPascalCase(string value) =>
        string.Concat(value.Select((character, index) =>
            index > 0 && char.IsUpper(character) ? $" {character}" : character.ToString()));

    private static HttpClient CreateDownloadClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        };
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("RSTT/1.0 (+https://github.com/helios19980520-max/RSTT)");
        return client;
    }

    public void Dispose() => _operationLock.Dispose();

    [LoggerMessage(LogLevel.Information, "Installed and verified model {ModelId} at {ModelDirectory}.")]
    private static partial void LogModelInstalled(ILogger logger, string modelId, string modelDirectory);

    [LoggerMessage(LogLevel.Information, "Deleted model {ModelId} from {ModelDirectory}.")]
    private static partial void LogModelDeleted(ILogger logger, string modelId, string modelDirectory);

    [LoggerMessage(LogLevel.Warning, "Unable to validate model installation in {ModelDirectory}.")]
    private static partial void LogModelValidationFailed(
        ILogger logger,
        Exception exception,
        string modelDirectory);

    [LoggerMessage(LogLevel.Error, "Model {ModelId} could not be downloaded or installed.")]
    private static partial void LogModelDownloadFailed(
        ILogger logger,
        Exception exception,
        string modelId);
}
