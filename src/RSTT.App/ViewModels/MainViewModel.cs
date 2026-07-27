using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using Microsoft.Extensions.Logging;
using RSTT.App.Infrastructure;
using RSTT.App.Services;
using RSTT.Core.Abstractions;
using RSTT.Core.Models;
using RSTT.Core.Settings;
using RSTT.Core.State;

namespace RSTT.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly RecognitionCoordinator _coordinator;
    private readonly IAudioCaptureService _audioCapture;
    private readonly IModelManager _modelManager;
    private readonly ISettingsService _settings;
    private readonly IAppPaths _paths;
    private readonly IApplicationStateService _applicationState;
    private readonly ILogger<MainViewModel> _logger;
    private CancellationTokenSource? _modelDownloadCancellation;
    private CancellationTokenSource? _settingsSaveDebounce;
    private string _statusText = "Initializing…";
    private string _previewStableText = string.Empty;
    private string _previewPendingText = string.Empty;
    private string _actionMessage = "Everything runs locally on this PC.";
    private string _downloadProgressText = string.Empty;
    private AudioDevice? _selectedAudioDevice;
    private ModelInformation? _selectedModel;
    private ApplicationState _currentState = ApplicationState.Initializing;
    private int _selectedPageIndex;
    private bool _isTextInjectionEnabled = true;
    private bool _isCaptionOverlayEnabled = true;
    private bool _minimizeToTray = true;
    private bool _startMinimized;
    private bool _autoStartListening;
    private bool _captionAlwaysOnTop = true;
    private bool _captionShowStableTextOnly;
    private bool _isDownloadingModel;
    private bool _isRefreshingDevices;
    private bool _isTestingAudio;
    private bool _isOverlayPreviewing;
    private bool _isInitializing = true;
    private double _captionFontSize = 28;
    private double _captionOpacity = 0.9;
    private double _captionWidth = 900;
    private double _audioLevel;
    private double _audioPeak;
    private double _downloadProgress;
    private bool _disposed;

    public MainViewModel(
        RecognitionCoordinator coordinator,
        IAudioCaptureService audioCapture,
        IModelManager modelManager,
        ISettingsService settings,
        IAppPaths paths,
        IApplicationStateService applicationState,
        ILogger<MainViewModel> logger)
    {
        _coordinator = coordinator;
        _audioCapture = audioCapture;
        _modelManager = modelManager;
        _settings = settings;
        _paths = paths;
        _applicationState = applicationState;
        _logger = logger;

        StartStopCommand = new AsyncRelayCommand(ToggleListeningAsync, CanToggleListening, ReportCommandFailure);
        RefreshDevicesCommand = new AsyncRelayCommand(RefreshAudioDevicesAsync, () => !IsRefreshingDevices, ReportCommandFailure);
        TestAudioCommand = new AsyncRelayCommand(TestAudioAsync, () => !IsTestingAudio && !IsListening, ReportCommandFailure);
        DownloadModelCommand = new AsyncRelayCommand(DownloadModelAsync, () => !IsDownloadingModel && !IsModelReady, ReportCommandFailure);
        CancelDownloadCommand = new RelayCommand(CancelModelDownload, () => IsDownloadingModel);
        DeleteModelCommand = new AsyncRelayCommand(DeleteModelAsync, () => IsModelReady && !IsDownloadingModel, ReportCommandFailure);
        RetryModelCommand = new AsyncRelayCommand(RetryModelAsync, () => !IsDownloadingModel, ReportCommandFailure);
        ResetSettingsCommand = new AsyncRelayCommand(ResetSettingsAsync, null, ReportCommandFailure);
        CopyTranscriptCommand = new RelayCommand(CopyTranscript, () => !string.IsNullOrWhiteSpace(TranscriptText));
        ClearTranscriptCommand = new RelayCommand(ClearTranscript, () => !string.IsNullOrWhiteSpace(TranscriptText));
        OpenModelsFolderCommand = new RelayCommand(() => OpenFolder(_paths.ModelsDirectory));
        OpenLogsFolderCommand = new RelayCommand(() => OpenFolder(_paths.LogsDirectory));
        NavigateToModelsCommand = new RelayCommand(() => SelectedPageIndex = 2);
        PreviewOverlayCommand = new RelayCommand(ToggleOverlayPreview);

        _coordinator.StateChanged += OnStateChanged;
        _coordinator.TranscriptUpdated += OnTranscriptUpdated;
        _audioCapture.AudioLevelChanged += OnAudioLevelChanged;
        _modelManager.ModelChanged += OnModelChanged;
    }

    public ObservableCollection<AudioDevice> AudioDevices { get; } = [];

    public ObservableCollection<ModelInformation> Models { get; } = [];

    public AsyncRelayCommand StartStopCommand { get; }

    public AsyncRelayCommand RefreshDevicesCommand { get; }

    public AsyncRelayCommand TestAudioCommand { get; }

    public AsyncRelayCommand DownloadModelCommand { get; }

    public RelayCommand CancelDownloadCommand { get; }

    public AsyncRelayCommand DeleteModelCommand { get; }

    public AsyncRelayCommand RetryModelCommand { get; }

    public AsyncRelayCommand ResetSettingsCommand { get; }

    public RelayCommand CopyTranscriptCommand { get; }

    public RelayCommand ClearTranscriptCommand { get; }

    public RelayCommand OpenModelsFolderCommand { get; }

    public RelayCommand OpenLogsFolderCommand { get; }

    public RelayCommand NavigateToModelsCommand { get; }

    public RelayCommand PreviewOverlayCommand { get; }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public ApplicationState CurrentState
    {
        get => _currentState;
        private set
        {
            if (SetProperty(ref _currentState, value))
            {
                OnPropertyChanged(nameof(IsListening));
                OnPropertyChanged(nameof(IsErrorState));
                OnPropertyChanged(nameof(StatusLabel));
            }
        }
    }

    public string StatusLabel => CurrentState switch
    {
        ApplicationState.Listening => "Listening",
        ApplicationState.Ready => "Ready",
        ApplicationState.ModelLoading => "Loading model",
        ApplicationState.ModelDownloading => "Downloading",
        ApplicationState.ModelMissing => "Setup needed",
        ApplicationState.Error => "Needs attention",
        ApplicationState.Stopping => "Stopping",
        _ => "Starting",
    };

    public bool IsListening => CurrentState == ApplicationState.Listening;

    public bool IsErrorState => CurrentState == ApplicationState.Error;

    public string PreviewStableText
    {
        get => _previewStableText;
        private set
        {
            if (SetProperty(ref _previewStableText, value))
            {
                RaiseTranscriptProperties();
            }
        }
    }

    public string PreviewPendingText
    {
        get => _previewPendingText;
        private set
        {
            if (SetProperty(ref _previewPendingText, value))
            {
                RaiseTranscriptProperties();
            }
        }
    }

    public string TranscriptText => string.Concat(PreviewStableText, PreviewPendingText).Trim();

    public string CaptionPendingText => CaptionShowStableTextOnly ? string.Empty : PreviewPendingText;

    public string OverlayStableText => IsOverlayPreviewing
        ? "RSTT captions stay visible without stealing focus. "
        : PreviewStableText;

    public string OverlayPendingText => IsOverlayPreviewing
        ? (CaptionShowStableTextOnly ? string.Empty : "Pending speech appears softer.")
        : CaptionPendingText;

    public bool HasTranscript => !string.IsNullOrWhiteSpace(TranscriptText);

    public string StartStopText => IsListening ? "Stop listening" : "Start listening";

    public string StartStopGlyph => IsListening ? "\uE71A" : "\uE768";

    public string ActionMessage
    {
        get => _actionMessage;
        private set => SetProperty(ref _actionMessage, value);
    }

    public int SelectedPageIndex
    {
        get => _selectedPageIndex;
        set => SetProperty(ref _selectedPageIndex, value);
    }

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
        set
        {
            if (SetProperty(ref _minimizeToTray, value))
            {
                _settings.Current.MinimizeToTray = value;
                PersistSettings();
            }
        }
    }

    public bool StartMinimized
    {
        get => _startMinimized;
        set
        {
            if (SetProperty(ref _startMinimized, value))
            {
                _settings.Current.StartMinimized = value;
                PersistSettings();
            }
        }
    }

    public bool AutoStartListening
    {
        get => _autoStartListening;
        set
        {
            if (SetProperty(ref _autoStartListening, value))
            {
                _settings.Current.AutoStartListening = value;
                PersistSettings();
            }
        }
    }

    public double CaptionFontSize
    {
        get => _captionFontSize;
        set
        {
            if (SetProperty(ref _captionFontSize, Math.Clamp(value, 18, 48)))
            {
                _settings.Current.CaptionFontSize = _captionFontSize;
                PersistSettings();
            }
        }
    }

    public double CaptionOpacity
    {
        get => _captionOpacity;
        set
        {
            if (SetProperty(ref _captionOpacity, Math.Clamp(value, 0.45, 1)))
            {
                _settings.Current.CaptionOpacity = _captionOpacity;
                PersistSettings();
            }
        }
    }

    public double CaptionWidth
    {
        get => _captionWidth;
        set
        {
            if (SetProperty(ref _captionWidth, Math.Clamp(value, 520, 1_400)))
            {
                _settings.Current.CaptionWidth = _captionWidth;
                PersistSettings();
            }
        }
    }

    public bool CaptionAlwaysOnTop
    {
        get => _captionAlwaysOnTop;
        set
        {
            if (SetProperty(ref _captionAlwaysOnTop, value))
            {
                _settings.Current.CaptionAlwaysOnTop = value;
                PersistSettings();
            }
        }
    }

    public bool CaptionShowStableTextOnly
    {
        get => _captionShowStableTextOnly;
        set
        {
            if (SetProperty(ref _captionShowStableTextOnly, value))
            {
                _settings.Current.CaptionShowStableTextOnly = value;
                OnPropertyChanged(nameof(CaptionPendingText));
                OnPropertyChanged(nameof(OverlayPendingText));
                PersistSettings();
            }
        }
    }

    public AudioDevice? SelectedAudioDevice
    {
        get => _selectedAudioDevice;
        set
        {
            if (SetProperty(ref _selectedAudioDevice, value))
            {
                _settings.Current.AudioDeviceId = value?.Id;
                OnPropertyChanged(nameof(SelectedAudioDeviceName));
                PersistSettings();
            }
        }
    }

    public string SelectedAudioDeviceName => SelectedAudioDevice?.Name ?? "No output device";

    public bool IsRefreshingDevices
    {
        get => _isRefreshingDevices;
        private set
        {
            if (SetProperty(ref _isRefreshingDevices, value))
            {
                RefreshDevicesCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsTestingAudio
    {
        get => _isTestingAudio;
        private set
        {
            if (SetProperty(ref _isTestingAudio, value))
            {
                OnPropertyChanged(nameof(TestAudioText));
                TestAudioCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string TestAudioText => IsTestingAudio ? "Testing… 5 s" : "Test audio";

    public double AudioLevel
    {
        get => _audioLevel;
        private set => SetProperty(ref _audioLevel, value);
    }

    public double AudioPeak
    {
        get => _audioPeak;
        private set => SetProperty(ref _audioPeak, value);
    }

    public bool IsOverlayPreviewing
    {
        get => _isOverlayPreviewing;
        private set
        {
            if (SetProperty(ref _isOverlayPreviewing, value))
            {
                OnPropertyChanged(nameof(OverlayStableText));
                OnPropertyChanged(nameof(OverlayPendingText));
            }
        }
    }

    public ModelInformation? SelectedModel
    {
        get => _selectedModel;
        private set
        {
            if (SetProperty(ref _selectedModel, value))
            {
                RaiseModelProperties();
            }
        }
    }

    public bool IsModelReady => SelectedModel?.IsInstalled == true && SelectedModel.Availability == ModelAvailability.Ready;

    public bool ShowOnboarding => !IsModelReady;

    public bool IsDownloadingModel
    {
        get => _isDownloadingModel;
        private set
        {
            if (SetProperty(ref _isDownloadingModel, value))
            {
                RaiseModelProperties();
            }
        }
    }

    public double DownloadProgress
    {
        get => _downloadProgress;
        private set => SetProperty(ref _downloadProgress, Math.Clamp(value, 0, 100));
    }

    public string DownloadProgressText
    {
        get => _downloadProgressText;
        private set => SetProperty(ref _downloadProgressText, value);
    }

    public string ModelSizeText => SelectedModel is null
        ? "—"
        : $"{SelectedModel.DownloadSizeBytes / 1024d / 1024d:N0} MB";

    public string ModelStatusText => SelectedModel?.Availability switch
    {
        ModelAvailability.Ready => "Ready",
        ModelAvailability.Downloading => "Downloading",
        ModelAvailability.Installing => "Installing",
        ModelAvailability.Validating => "Validating",
        ModelAvailability.Invalid => "Needs repair",
        ModelAvailability.Error => "Download failed",
        ModelAvailability.NotInstalled => "Not installed",
        _ => "Checking…",
    };

    public string ModelStatusGlyph => IsModelReady ? "\uE73E" : "\uE946";

    public bool CanDeleteModel => IsModelReady && !IsDownloadingModel;

    public async Task InitializeAsync()
    {
        await _settings.LoadAsync().ConfigureAwait(true);
        ApplySettings(_settings.Current);
        RefreshModels();
        await RefreshAudioDevicesAsync().ConfigureAwait(true);
        _isInitializing = false;

        await _coordinator.InitializeAsync().ConfigureAwait(true);
        if (!IsModelReady)
        {
            SelectedPageIndex = 2;
            ActionMessage = "Install the recommended local model to start transcribing.";
        }
        else if (_settings.Current.AutoStartListening && _applicationState.Current.State == ApplicationState.Ready)
        {
            await _coordinator.StartAsync().ConfigureAwait(true);
        }
    }

    public void ToggleTextInjection() => IsTextInjectionEnabled = !IsTextInjectionEnabled;

    public void ToggleCaptionOverlay() => IsCaptionOverlayEnabled = !IsCaptionOverlayEnabled;

    public void ReportHotkeyRegistrationFailure() =>
        ActionMessage = "One or more global shortcuts are already in use by another app.";

    public async Task FlushSettingsAsync()
    {
        _settingsSaveDebounce?.Cancel();
        if (!_isInitializing)
        {
            await _settings.SaveAsync().ConfigureAwait(false);
        }
    }

    private async Task ToggleListeningAsync()
    {
        if (_coordinator.IsListening)
        {
            await _coordinator.StopAsync().ConfigureAwait(true);
            ActionMessage = "Listening stopped. Your model remains ready.";
        }
        else
        {
            await _coordinator.StartAsync().ConfigureAwait(true);
            if (_coordinator.IsListening)
            {
                ActionMessage = "Capturing system output and transcribing locally.";
            }
        }

        RaiseListeningProperties();
    }

    private bool CanToggleListening() =>
        _applicationState.Current.State is ApplicationState.Ready or ApplicationState.Listening;

    private async Task RefreshAudioDevicesAsync()
    {
        if (IsRefreshingDevices)
        {
            return;
        }

        IsRefreshingDevices = true;
        try
        {
            var selectedId = SelectedAudioDevice?.Id ?? _settings.Current.AudioDeviceId;
            var devices = await _audioCapture.GetOutputDevicesAsync().ConfigureAwait(true);
            AudioDevices.Clear();
            foreach (var device in devices)
            {
                AudioDevices.Add(device);
            }

            SelectedAudioDevice = AudioDevices.FirstOrDefault(device => device.Id == selectedId)
                ?? AudioDevices.FirstOrDefault(device => device.IsDefault)
                ?? AudioDevices.FirstOrDefault();
            ActionMessage = AudioDevices.Count == 0
                ? "No active Windows output device was found."
                : $"Audio source ready: {SelectedAudioDeviceName}";
        }
        catch (Exception exception)
        {
            LogAudioDeviceEnumerationFailed(_logger, exception);
            StatusText = "Audio device unavailable";
            ActionMessage = exception.Message;
        }
        finally
        {
            IsRefreshingDevices = false;
        }
    }

    private async Task TestAudioAsync()
    {
        if (IsTestingAudio || IsListening)
        {
            return;
        }

        IsTestingAudio = true;
        using var cancellation = new CancellationTokenSource();
        try
        {
            await _audioCapture.StartAsync(SelectedAudioDevice?.Id, cancellation.Token).ConfigureAwait(true);
            ActionMessage = "Testing the selected Windows output. Play any sound to move the meter.";
            var drainTask = Task.Run(
                async () =>
                {
                    try
                    {
                        await foreach (var _ in _audioCapture.ReadChunksAsync(cancellation.Token).ConfigureAwait(false))
                        {
                        }
                    }
                    catch (OperationCanceledException)
                    {
                    }
                },
                CancellationToken.None);
            await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
            cancellation.Cancel();
            await _audioCapture.StopAsync().ConfigureAwait(true);
            await drainTask.ConfigureAwait(true);
            ActionMessage = "Audio test complete. The live meter used captured WASAPI samples.";
        }
        finally
        {
            cancellation.Cancel();
            await _audioCapture.StopAsync().ConfigureAwait(true);
            IsTestingAudio = false;
        }
    }

    private async Task DownloadModelAsync()
    {
        if (SelectedModel is null || IsDownloadingModel)
        {
            return;
        }

        IsDownloadingModel = true;
        DownloadProgress = 0;
        DownloadProgressText = "Preparing secure download…";
        _modelDownloadCancellation?.Dispose();
        _modelDownloadCancellation = new CancellationTokenSource();
        _applicationState.TransitionTo(ApplicationState.ModelDownloading, "Downloading local speech model…");

        var progress = new Progress<ModelDownloadProgress>(update =>
        {
            DownloadProgress = update.Fraction * 100;
            DownloadProgressText = update.TotalBytes <= 0
                ? update.Message
                : $"{update.Message}  {update.BytesReceived / 1024d / 1024d:N0} / {update.TotalBytes / 1024d / 1024d:N0} MB";
        });

        try
        {
            await _modelManager.DownloadAsync(SelectedModel.Id, progress, _modelDownloadCancellation.Token).ConfigureAwait(true);
            RefreshModels();
            DownloadProgress = 100;
            DownloadProgressText = "Verified and installed";
            await _coordinator.ReloadModelAsync().ConfigureAwait(true);
            ActionMessage = "Parakeet is installed and ready. Press Start listening.";
            SelectedPageIndex = 0;
        }
        catch (OperationCanceledException)
        {
            DownloadProgressText = "Download paused — start again to resume";
            _applicationState.TransitionTo(ApplicationState.ModelMissing, "Model download paused");
            ActionMessage = "The partial download was kept safely and can be resumed.";
        }
        finally
        {
            IsDownloadingModel = false;
            _modelDownloadCancellation?.Dispose();
            _modelDownloadCancellation = null;
            RefreshModels();
        }
    }

    private void CancelModelDownload() => _modelDownloadCancellation?.Cancel();

    private async Task DeleteModelAsync()
    {
        if (SelectedModel is null || !IsModelReady)
        {
            return;
        }

        var answer = System.Windows.MessageBox.Show(
            $"Delete {SelectedModel.DisplayName} and its {ModelSizeText} of local model files?",
            "Delete local model",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        await _coordinator.UnloadModelAsync().ConfigureAwait(true);
        await _modelManager.DeleteAsync(SelectedModel.Id).ConfigureAwait(true);
        RefreshModels();
        SelectedPageIndex = 2;
        ActionMessage = "The local model was deleted. Install it again whenever you need it.";
    }

    private async Task RetryModelAsync()
    {
        RefreshModels();
        if (IsModelReady)
        {
            await _coordinator.ReloadModelAsync().ConfigureAwait(true);
            ActionMessage = "Model validation passed and the recognizer was reloaded.";
        }
        else
        {
            await DownloadModelAsync().ConfigureAwait(true);
        }
    }

    private async Task ResetSettingsAsync()
    {
        var defaults = new AppSettings { HasCompletedOnboarding = _settings.Current.HasCompletedOnboarding };
        _isInitializing = true;
        CopySettings(defaults, _settings.Current);
        ApplySettings(_settings.Current);
        _isInitializing = false;
        await _settings.SaveAsync().ConfigureAwait(true);
        ActionMessage = "Settings restored to safe defaults.";
    }

    private void CopyTranscript()
    {
        if (!string.IsNullOrWhiteSpace(TranscriptText))
        {
            System.Windows.Clipboard.SetText(TranscriptText);
            ActionMessage = "Transcript copied to the clipboard.";
        }
    }

    private void ClearTranscript()
    {
        PreviewStableText = string.Empty;
        PreviewPendingText = string.Empty;
        ActionMessage = "Transcript preview cleared.";
    }

    private void ToggleOverlayPreview()
    {
        IsOverlayPreviewing = !IsOverlayPreviewing;
        if (IsOverlayPreviewing)
        {
            IsCaptionOverlayEnabled = true;
            ActionMessage = "Overlay preview is active and does not take keyboard focus.";
        }
        else
        {
            ActionMessage = "Overlay preview closed.";
        }
    }

    private void ApplySettings(AppSettings settings)
    {
        _isTextInjectionEnabled = settings.TextInjectionEnabled;
        _isCaptionOverlayEnabled = settings.CaptionOverlayEnabled;
        _minimizeToTray = settings.MinimizeToTray;
        _startMinimized = settings.StartMinimized;
        _autoStartListening = settings.AutoStartListening;
        _captionFontSize = Math.Clamp(settings.CaptionFontSize, 18, 48);
        _captionOpacity = Math.Clamp(settings.CaptionOpacity, 0.45, 1);
        _captionWidth = Math.Clamp(settings.CaptionWidth, 520, 1_400);
        _captionAlwaysOnTop = settings.CaptionAlwaysOnTop;
        _captionShowStableTextOnly = settings.CaptionShowStableTextOnly;
        OnPropertyChanged(string.Empty);
    }

    private static void CopySettings(AppSettings source, AppSettings destination)
    {
        destination.AudioDeviceId = source.AudioDeviceId;
        destination.SpeechEngine = source.SpeechEngine;
        destination.SpeechModel = source.SpeechModel;
        destination.Language = source.Language;
        destination.TextInjectionEnabled = source.TextInjectionEnabled;
        destination.CaptionOverlayEnabled = source.CaptionOverlayEnabled;
        destination.StartMinimized = source.StartMinimized;
        destination.MinimizeToTray = source.MinimizeToTray;
        destination.AutoStartListening = source.AutoStartListening;
        destination.CaptionFontSize = source.CaptionFontSize;
        destination.CaptionOpacity = source.CaptionOpacity;
        destination.CaptionWidth = source.CaptionWidth;
        destination.CaptionAlwaysOnTop = source.CaptionAlwaysOnTop;
        destination.CaptionShowStableTextOnly = source.CaptionShowStableTextOnly;
        destination.ToggleListeningHotkey = source.ToggleListeningHotkey;
        destination.ToggleInjectionHotkey = source.ToggleInjectionHotkey;
        destination.ToggleCaptionsHotkey = source.ToggleCaptionsHotkey;
    }

    private void RefreshModels()
    {
        var selectedId = _settings.Current.SpeechModel;
        Models.Clear();
        foreach (var model in _modelManager.GetAvailableModels())
        {
            Models.Add(model);
        }

        SelectedModel = Models.FirstOrDefault(model => string.Equals(model.Id, selectedId, StringComparison.OrdinalIgnoreCase))
            ?? Models.FirstOrDefault();
    }

    private void OnStateChanged(object? sender, ApplicationStateSnapshot state)
    {
        System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            CurrentState = state.State;
            StatusText = state.StatusMessage;
            if (state.Error is not null)
            {
                ActionMessage = state.Error.Message;
            }

            RaiseListeningProperties();
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

    private void OnAudioLevelChanged(object? sender, AudioLevelEventArgs eventArgs)
    {
        System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            AudioLevel = eventArgs.Level;
            AudioPeak = eventArgs.Peak;
        });
    }

    private void OnModelChanged(object? sender, ModelInformation model)
    {
        System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            RefreshModels();
            if (string.Equals(SelectedModel?.Id, model.Id, StringComparison.OrdinalIgnoreCase))
            {
                SelectedModel = model;
            }
        });
    }

    private void RaiseListeningProperties()
    {
        OnPropertyChanged(nameof(StartStopText));
        OnPropertyChanged(nameof(StartStopGlyph));
        OnPropertyChanged(nameof(IsListening));
        StartStopCommand.RaiseCanExecuteChanged();
    }

    private void RaiseTranscriptProperties()
    {
        OnPropertyChanged(nameof(TranscriptText));
        OnPropertyChanged(nameof(HasTranscript));
        OnPropertyChanged(nameof(CaptionPendingText));
        OnPropertyChanged(nameof(OverlayStableText));
        OnPropertyChanged(nameof(OverlayPendingText));
        CopyTranscriptCommand.RaiseCanExecuteChanged();
        ClearTranscriptCommand.RaiseCanExecuteChanged();
    }

    private void RaiseModelProperties()
    {
        OnPropertyChanged(nameof(IsModelReady));
        OnPropertyChanged(nameof(ShowOnboarding));
        OnPropertyChanged(nameof(ModelSizeText));
        OnPropertyChanged(nameof(ModelStatusText));
        OnPropertyChanged(nameof(ModelStatusGlyph));
        OnPropertyChanged(nameof(CanDeleteModel));
        DownloadModelCommand.RaiseCanExecuteChanged();
        CancelDownloadCommand.RaiseCanExecuteChanged();
        DeleteModelCommand.RaiseCanExecuteChanged();
        RetryModelCommand.RaiseCanExecuteChanged();
        StartStopCommand.RaiseCanExecuteChanged();
        TestAudioCommand.RaiseCanExecuteChanged();
    }

    private void PersistSettings()
    {
        if (_isInitializing || _disposed)
        {
            return;
        }

        _settingsSaveDebounce?.Cancel();
        _settingsSaveDebounce?.Dispose();
        _settingsSaveDebounce = new CancellationTokenSource();
        _ = PersistSettingsAsync(_settingsSaveDebounce.Token);
    }

    private async Task PersistSettingsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(300, cancellationToken).ConfigureAwait(false);
            await _settings.SaveAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            LogSettingsSaveFailed(_logger, exception);
        }
    }

    private void ReportCommandFailure(Exception exception)
    {
        LogCommandFailed(_logger, exception);
        StatusText = "Action failed";
        ActionMessage = exception.Message;
    }

    private static void OpenFolder(string path)
    {
        System.IO.Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _settingsSaveDebounce?.Cancel();
        _settingsSaveDebounce?.Dispose();
        _modelDownloadCancellation?.Cancel();
        _modelDownloadCancellation?.Dispose();
        _coordinator.StateChanged -= OnStateChanged;
        _coordinator.TranscriptUpdated -= OnTranscriptUpdated;
        _audioCapture.AudioLevelChanged -= OnAudioLevelChanged;
        _modelManager.ModelChanged -= OnModelChanged;
        GC.SuppressFinalize(this);
    }

    [LoggerMessage(LogLevel.Warning, "Output device enumeration failed.")]
    private static partial void LogAudioDeviceEnumerationFailed(ILogger logger, Exception exception);

    [LoggerMessage(LogLevel.Warning, "Settings could not be saved.")]
    private static partial void LogSettingsSaveFailed(ILogger logger, Exception exception);

    [LoggerMessage(LogLevel.Error, "A UI command failed.")]
    private static partial void LogCommandFailed(ILogger logger, Exception exception);
}
