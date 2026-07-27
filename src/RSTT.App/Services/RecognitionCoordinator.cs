using Microsoft.Extensions.Logging;
using RSTT.Core.Abstractions;
using RSTT.Core.Models;
using RSTT.Core.State;
using RSTT.Core.Transcription;

namespace RSTT.App.Services;

/// <summary>Coordinates the bounded audio worker, local ASR, captions, and safe text output.</summary>
public sealed partial class RecognitionCoordinator : IAsyncDisposable
{
    private readonly IAudioCaptureService _audioCapture;
    private readonly ISpeechRecognitionEngine _speechEngine;
    private readonly ITextInjectionService _textInjection;
    private readonly ISettingsService _settings;
    private readonly IApplicationStateService _applicationState;
    private readonly TranscriptStabilizer _stabilizer;
    private readonly ILogger<RecognitionCoordinator> _logger;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private CancellationTokenSource? _listeningCancellation;
    private Task? _audioWorker;
    private bool _isListening;

    public RecognitionCoordinator(
        IAudioCaptureService audioCapture,
        ISpeechRecognitionEngine speechEngine,
        ITextInjectionService textInjection,
        ISettingsService settings,
        IApplicationStateService applicationState,
        TranscriptStabilizer stabilizer,
        ILogger<RecognitionCoordinator> logger)
    {
        _audioCapture = audioCapture;
        _speechEngine = speechEngine;
        _textInjection = textInjection;
        _settings = settings;
        _applicationState = applicationState;
        _stabilizer = stabilizer;
        _logger = logger;
        _speechEngine.RecognitionResultAvailable += OnRecognitionResultAvailable;
    }

    public bool IsListening => _isListening;

    public event EventHandler<TranscriptUpdate>? TranscriptUpdated;

    public event EventHandler<ApplicationStateSnapshot>? StateChanged
    {
        add => _applicationState.Changed += value;
        remove => _applicationState.Changed -= value;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _applicationState.TransitionTo(ApplicationState.ModelLoading, "Loading local speech model…");
        try
        {
            await _speechEngine.InitializeAsync(cancellationToken).ConfigureAwait(false);
            _applicationState.TransitionTo(ApplicationState.Ready, "Model ready");
        }
        catch (InvalidOperationException exception)
        {
            _applicationState.TransitionTo(ApplicationState.ModelMissing, exception.Message, exception);
            LogModelNotReady(_logger, exception.Message);
        }
        catch (Exception exception)
        {
            _applicationState.TransitionTo(ApplicationState.Error, "Speech model could not be loaded.", exception);
            LogEngineInitializationFailed(_logger, exception);
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_isListening)
            {
                return;
            }

            _listeningCancellation?.Dispose();
            _listeningCancellation = null;

            if (!_speechEngine.IsReady)
            {
                _applicationState.TransitionTo(ApplicationState.ModelMissing, _speechEngine.ModelInformation.StatusMessage ?? "No local speech model is installed.");
                return;
            }

            _stabilizer.Reset();
            await _speechEngine.ResetAsync(cancellationToken).ConfigureAwait(false);
            await _speechEngine.StartAsync(cancellationToken).ConfigureAwait(false);
            _listeningCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            await _audioCapture.StartAsync(_settings.Current.AudioDeviceId, _listeningCancellation.Token).ConfigureAwait(false);
            _isListening = true;
            _audioWorker = Task.Run(() => ProcessAudioAsync(_listeningCancellation.Token), CancellationToken.None);
            _applicationState.TransitionTo(ApplicationState.Listening, "Listening to system audio");
        }
        catch (Exception exception)
        {
            _applicationState.TransitionTo(ApplicationState.Error, "Audio capture could not be started.", exception);
            LogPipelineStartFailed(_logger, exception);
            await StopUnsafeAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_isListening && _audioWorker is null)
            {
                return;
            }

            _applicationState.TransitionTo(ApplicationState.Stopping, "Stopping…");
            await StopUnsafeAsync().ConfigureAwait(false);
            _applicationState.TransitionTo(ApplicationState.Ready, _speechEngine.IsReady ? "Model ready" : "No local speech model is installed.");
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private async Task ProcessAudioAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var chunk in _audioCapture.ReadChunksAsync(cancellationToken).ConfigureAwait(false))
            {
                await _speechEngine.ProcessAudioAsync(chunk, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _isListening = false;
            _applicationState.TransitionTo(ApplicationState.Error, "The audio pipeline stopped unexpectedly.", exception);
            LogWorkerFailed(_logger, exception);
        }
    }

    private async Task StopUnsafeAsync()
    {
        _isListening = false;
        _listeningCancellation?.Cancel();
        await _audioCapture.StopAsync().ConfigureAwait(false);
        if (_audioWorker is not null)
        {
            try
            {
                await _audioWorker.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _audioWorker = null;
        _listeningCancellation?.Dispose();
        _listeningCancellation = null;
        await _speechEngine.StopAsync().ConfigureAwait(false);
    }

    private void OnRecognitionResultAvailable(object? sender, RecognitionResult result)
    {
        _ = PublishRecognitionResultAsync(result);
    }

    private async Task PublishRecognitionResultAsync(RecognitionResult result)
    {
        try
        {
            var update = _stabilizer.Process(result);
            TranscriptUpdated?.Invoke(this, update);
            if (_isListening && _settings.Current.TextInjectionEnabled && !string.IsNullOrWhiteSpace(update.NewlyStableText))
            {
                var injectionResult = await _textInjection.InjectTextAsync(update.NewlyStableText).ConfigureAwait(false);
                if (!injectionResult.Succeeded)
                {
                    LogInjectionNotCompleted(_logger, injectionResult.Message ?? "No diagnostic was provided.");
                }
            }
        }
        catch (Exception exception)
        {
            LogResultProcessingFailed(_logger, exception);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _speechEngine.RecognitionResultAvailable -= OnRecognitionResultAvailable;
        await StopAsync().ConfigureAwait(false);
        _lifecycleLock.Dispose();
    }

    [LoggerMessage(LogLevel.Information, "Local speech model is not ready: {Message}")]
    private static partial void LogModelNotReady(ILogger logger, string message);

    [LoggerMessage(LogLevel.Error, "Failed to initialize local speech engine.")]
    private static partial void LogEngineInitializationFailed(ILogger logger, Exception exception);

    [LoggerMessage(LogLevel.Error, "Failed to start the audio recognition pipeline.")]
    private static partial void LogPipelineStartFailed(ILogger logger, Exception exception);

    [LoggerMessage(LogLevel.Error, "Audio recognition worker terminated unexpectedly.")]
    private static partial void LogWorkerFailed(ILogger logger, Exception exception);

    [LoggerMessage(LogLevel.Warning, "Text injection was not completed: {Message}")]
    private static partial void LogInjectionNotCompleted(ILogger logger, string message);

    [LoggerMessage(LogLevel.Error, "Recognition result processing failed; captions will continue on later results.")]
    private static partial void LogResultProcessingFailed(ILogger logger, Exception exception);
}
