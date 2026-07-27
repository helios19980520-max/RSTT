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
            await using var stream = File.OpenRead(_paths.SettingsFilePath);
            Current = await JsonSerializer.DeserializeAsync<AppSettings>(stream, SerializerOptions, cancellationToken).ConfigureAwait(false) ?? new AppSettings();
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

    [LoggerMessage(LogLevel.Warning, "Settings file is malformed. RSTT will use safe defaults.")]
    private static partial void LogMalformedSettings(ILogger logger, Exception exception);

    [LoggerMessage(LogLevel.Warning, "Settings could not be read. RSTT will use safe defaults.")]
    private static partial void LogUnreadableSettings(ILogger logger, Exception exception);
}
