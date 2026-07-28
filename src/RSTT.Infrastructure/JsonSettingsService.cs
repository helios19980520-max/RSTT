using System.Text.Json;
using Microsoft.Extensions.Logging;
using RSTT.Core.Abstractions;
using RSTT.Core.Settings;

namespace RSTT.Infrastructure;

public sealed partial class JsonSettingsService : ISettingsService, IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly IAppPaths _paths;
    private readonly ILogger<JsonSettingsService> _logger;
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    public JsonSettingsService(IAppPaths paths, ILogger<JsonSettingsService> logger)
    {
        _paths = paths;
        _logger = logger;
    }

    public AppSettings Current { get; private set; } = new();

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        _paths.EnsureDirectoriesExist();
        if (!File.Exists(_paths.SettingsFilePath))
        {
            Current = new AppSettings();
            return;
        }

        try
        {
            var json = await File.ReadAllTextAsync(
                    _paths.SettingsFilePath,
                    cancellationToken)
                .ConfigureAwait(false);
            Current = JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions) ??
                new AppSettings();
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!HasProperty(root, nameof(AppSettings.DefaultModelId)))
            {
                Current.DefaultModelId = Current.SpeechModel;
            }

            if (!HasProperty(root, nameof(AppSettings.DefaultLanguage)))
            {
                Current.DefaultLanguage = Current.Language;
            }

            if (!HasProperty(root, nameof(AppSettings.DefaultBackend)))
            {
                Current.DefaultBackend = Current.ComputeBackend;
            }

            Current.SpeechModel = Current.DefaultModelId;
            Current.Language = Current.DefaultLanguage;
            Current.ComputeBackend = Current.DefaultBackend;
        }
        catch (JsonException exception)
        {
            Current = new AppSettings();
            LogMalformedSettings(_logger, exception);
        }
        catch (IOException exception)
        {
            Current = new AppSettings();
            LogUnreadableSettings(_logger, exception);
        }
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        await _saveLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _paths.EnsureDirectoriesExist();
            var temporaryPath = _paths.SettingsFilePath + ".tmp";
            await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 16 * 1024, true))
            {
                await JsonSerializer.SerializeAsync(stream, Current, SerializerOptions, cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, _paths.SettingsFilePath, true);
        }
        finally
        {
            _saveLock.Release();
        }
    }

    public void Dispose() => _saveLock.Dispose();

    private static bool HasProperty(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object &&
        root.EnumerateObject().Any(property =>
            property.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    [LoggerMessage(LogLevel.Warning, "Settings file is malformed. RSTT will use safe defaults.")]
    private static partial void LogMalformedSettings(ILogger logger, Exception exception);

    [LoggerMessage(LogLevel.Warning, "Settings could not be read. RSTT will use safe defaults.")]
    private static partial void LogUnreadableSettings(ILogger logger, Exception exception);
}
