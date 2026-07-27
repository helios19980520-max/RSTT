using System.Collections.ObjectModel;
using System.Windows;
using Microsoft.Extensions.Logging;
using RSTT.App.Infrastructure;
using RSTT.App.Services;
using RSTT.Core.Abstractions;
using RSTT.Core.Models;
using RSTT.Core.State;

namespace RSTT.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly RecognitionCoordinator _coordinator;
    private readonly IAudioCaptureService _audioCapture;
    private readonly ISettingsService _settings;
    private readonly IApplicationStateService _applicationState;
    private readonly ILogger<MainViewModel> _logger;
    private string _statusText = "Initializing…";
    private string _previewStableText = string.Empty;
    private string _previewPendingText = string.Empty;
    private AudioDevice? _selectedAudioDevice;
    private bool _isTextInjectionEnabled = true;
    private bool _isCaptionOverlayEnabled = true;
    private bool _minimizeToTray = true;

    public MainViewModel(
        RecognitionCoordinator coordinator,
        IAudioCaptureService audioCapture,
        ISettingsService settings,
        IApplicationStateService applicationState,
        ILogger<MainViewModel> logger)
    {
        _coordinator = coordinator;
        _audioCapture = audioCapture;
        _settings = settings;
        _applicationState = applicationState;
        _logger = logger;
        StartStopCommand = new AsyncRelayCommand(ToggleListeningAsync, CanToggleListening);
        _coordinator.StateChanged += OnStateChanged;
        _coordinator.TranscriptUpdated += OnTranscriptUpdated;
    }

    public ObservableCollection<AudioDevice> AudioDevices { get; } = [];

    public AsyncRelayCommand StartStopCommand { get; }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string PreviewStableText
    {
        get => _previewStableText;
        private set => SetProperty(ref _previewStableText, value);
    }

    public string PreviewPendingText
    {
        get => _previewPendingText;
        private set => SetProperty(ref _previewPendingText, value);
    }

    public string StartStopText => _coordinator.IsListening ? "Stop Listening" : "Start Listening";

    public bool IsTextInjectionEnabled
    {
        get => _isTextInjectionEnabled;
        set
        {
            if (SetProperty(ref _isTextInjectionEnabled, value))
            {
                _settings.Current.TextInjectionEnabled = value;
                PersistSettings();
            }
        }
    }

    public bool IsCaptionOverlayEnabled
    {
        get => _isCaptionOverlayEnabled;
        set
        {
            if (SetProperty(ref _isCaptionOverlayEnabled, value))
            {
                _settings.Current.CaptionOverlayEnabled = value;
                PersistSettings();
            }
        }
    }

    public bool MinimizeToTray
    {
        get => _minimizeToTray;
        private set => SetProperty(ref _minimizeToTray, value);
    }

    public AudioDevice? SelectedAudioDevice
    {
        get => _selectedAudioDevice;
        set
        {
            if (SetProperty(ref _selectedAudioDevice, value))
            {
                _settings.Current.AudioDeviceId = value?.Id;
                PersistSettings();
            }
        }
    }

    public async Task InitializeAsync()
    {
        await _settings.LoadAsync();
        IsTextInjectionEnabled = _settings.Current.TextInjectionEnabled;
        IsCaptionOverlayEnabled = _settings.Current.CaptionOverlayEnabled;
        MinimizeToTray = _settings.Current.MinimizeToTray;

        try
        {
            var devices = await _audioCapture.GetOutputDevicesAsync();
            foreach (var device in devices)
            {
                AudioDevices.Add(device);
            }

            SelectedAudioDevice = AudioDevices.FirstOrDefault(device => device.Id == _settings.Current.AudioDeviceId)
                ?? AudioDevices.FirstOrDefault(device => device.IsDefault)
                ?? AudioDevices.FirstOrDefault();
        }
        catch (Exception exception)
        {
            LogAudioDeviceEnumerationFailed(_logger, exception);
            StatusText = "Audio device unavailable";
        }

        await _coordinator.InitializeAsync();
        if (_settings.Current.AutoStartListening && _applicationState.Current.State == ApplicationState.Ready)
        {
            await _coordinator.StartAsync();
        }
    }

    public void ToggleTextInjection() => IsTextInjectionEnabled = !IsTextInjectionEnabled;

    public void ToggleCaptionOverlay() => IsCaptionOverlayEnabled = !IsCaptionOverlayEnabled;

    private async Task ToggleListeningAsync()
    {
        if (_coordinator.IsListening)
        {
            await _coordinator.StopAsync();
        }
        else
        {
            await _coordinator.StartAsync();
        }

        OnPropertyChanged(nameof(StartStopText));
        StartStopCommand.RaiseCanExecuteChanged();
    }

    private bool CanToggleListening() => _applicationState.Current.State is ApplicationState.Ready or ApplicationState.Listening or ApplicationState.Error;

    private void OnStateChanged(object? sender, ApplicationStateSnapshot state)
    {
        System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            StatusText = state.StatusMessage;
            OnPropertyChanged(nameof(StartStopText));
            StartStopCommand.RaiseCanExecuteChanged();
        });
    }

    private void OnTranscriptUpdated(object? sender, TranscriptUpdate update)
    {
        System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            PreviewStableText = update.StableText;
            PreviewPendingText = update.PendingText;
        });
    }

    private void PersistSettings() => _ = PersistSettingsAsync();

    private async Task PersistSettingsAsync()
    {
        try
        {
            await _settings.SaveAsync();
        }
        catch (Exception exception)
        {
            LogSettingsSaveFailed(_logger, exception);
        }
    }

    [LoggerMessage(LogLevel.Warning, "Output device enumeration failed during startup.")]
    private static partial void LogAudioDeviceEnumerationFailed(ILogger logger, Exception exception);

    [LoggerMessage(LogLevel.Warning, "Settings could not be saved.")]
    private static partial void LogSettingsSaveFailed(ILogger logger, Exception exception);
}
