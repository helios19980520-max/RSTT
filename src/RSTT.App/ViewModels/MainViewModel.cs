using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using RSTT.App.Infrastructure;
using RSTT.App.Services;
using RSTT.Core.Abstractions;
using RSTT.Core.Models;
using RSTT.Core.Settings;
using RSTT.Core.State;
using RSTT.Core.Transcription;

namespace RSTT.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly RecognitionCoordinator _coordinator;
    private readonly IAudioCaptureService _audioCapture;
    private readonly IModelManager _modelManager;
    private readonly ISettingsService _settings;
    private readonly IAppPaths _paths;
    private readonly IApplicationStateService _applicationState;
    private readonly CaptionHistory _captionHistory;
    private readonly IPerformanceMonitor _performance;
    private readonly IComputeDeviceService _computeDevices;
    private readonly ISpeechEngineFactory _speechEngineFactory;
    private readonly ILogger<MainViewModel> _logger;
    private readonly DispatcherTimer _telemetryTimer;
    private CancellationTokenSource? _modelDownloadCancellation;
    private CancellationTokenSource? _settingsSaveDebounce;
    private string _statusText = "Initializing…";
    private string _previewStableText = string.Empty;
    private string _previewPendingText = string.Empty;
    private string _actionMessage = "Everything runs locally on this PC.";
    private string _downloadProgressText = string.Empty;
    private string _toggleListeningHotkey = "Ctrl+Alt+R";
    private string _toggleInjectionHotkey = "Ctrl+Alt+T";
    private string _toggleCaptionsHotkey = "Ctrl+Alt+C";
    private string _toggleListeningHotkeyError = string.Empty;
    private string _toggleInjectionHotkeyError = string.Empty;
    private string _toggleCaptionsHotkeyError = string.Empty;
    private string _computeStatus = "Checking available compute backends…";
    private string _modelSearchText = string.Empty;
    private string _selectedModelFilter = "All";
    private string _diagnosticsText = "Run the self-test to collect system, graphics, CUDA, ASR, audio, and performance status.";
    private AudioDevice? _selectedAudioDevice;
    private ModelInformation? _selectedModel;
    private CaptionSegment? _currentCaption;
    private PerformanceSnapshot? _performanceSnapshot;
    private ApplicationState _currentState = ApplicationState.Initializing;
    private ComputeBackend _computeBackend = ComputeBackend.Auto;
    private RecognitionMode _recognitionMode = RecognitionMode.Balanced;
    private TextInjectionDeliveryMode _textInjectionDeliveryMode =
        TextInjectionDeliveryMode.Automatic;
    private int _selectedPageIndex;
    private bool _isTextInjectionEnabled = true;
    private bool _isCaptionOverlayEnabled = true;
    private bool _minimizeToTray = true;
    private bool _startMinimized;
    private bool _autoStartListening;
    private bool _captionAlwaysOnTop = true;
    private bool _captionShowStableTextOnly;
    private bool _captionPositionLocked = true;
    private bool _captionShowStatusIndicator = true;
    private bool _isDownloadingModel;
    private bool _isRefreshingDevices;
    private bool _isTestingAudio;
    private bool _isRunningDiagnostics;
    private bool _isOverlayPreviewing;
    private bool _isInitializing = true;
    private double _captionFontSize = 28;
    private double _captionOpacity = 0.9;
    private double _captionWidth = 900;
    private double _captionHeight = 210;
    private double? _captionLeft;
    private double? _captionTop;
    private int _captionMaximumLines = 4;
    private double _captionLineSpacing = 1.2;
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
        CaptionHistory captionHistory,
        IPerformanceMonitor performance,
        IComputeDeviceService computeDevices,
        ISpeechEngineFactory speechEngineFactory,
        ILogger<MainViewModel> logger)
    {
        _coordinator = coordinator;
        _audioCapture = audioCapture;
        _modelManager = modelManager;
        _settings = settings;
        _paths = paths;
        _applicationState = applicationState;
        _captionHistory = captionHistory;
        _performance = performance;
        _computeDevices = computeDevices;
        _speechEngineFactory = speechEngineFactory;
        _logger = logger;
        _telemetryTimer = new DispatcherTimer(
            TimeSpan.FromSeconds(1),
            DispatcherPriority.Background,
            OnTelemetryTick,
            System.Windows.Application.Current.Dispatcher);
        _telemetryTimer.Stop();

        StartStopCommand = new AsyncRelayCommand(ToggleListeningAsync, CanToggleListening, ReportCommandFailure);
        RefreshDevicesCommand = new AsyncRelayCommand(RefreshAudioDevicesAsync, () => !IsRefreshingDevices, ReportCommandFailure);
        TestAudioCommand = new AsyncRelayCommand(TestAudioAsync, () => !IsTestingAudio && !IsListening, ReportCommandFailure);
        DownloadModelCommand = new AsyncRelayCommand(
            DownloadModelAsync,
            () => CanDownloadSelectedModel,
            ReportCommandFailure);
        UseModelCommand = new AsyncRelayCommand(
            UseSelectedModelAsync,
            () => CanUseSelectedModel,
            ReportCommandFailure);
        SetDefaultModelCommand = new AsyncRelayCommand(
            SetDefaultModelAsync,
            () => CanSetSelectedModelDefault,
            ReportCommandFailure);
        CancelDownloadCommand = new RelayCommand(CancelModelDownload, () => IsDownloadingModel);
        DeleteModelCommand = new AsyncRelayCommand(DeleteModelAsync, () => IsModelReady && !IsDownloadingModel, ReportCommandFailure);
        RetryModelCommand = new AsyncRelayCommand(RetryModelAsync, () => !IsDownloadingModel, ReportCommandFailure);
        ResetSettingsCommand = new AsyncRelayCommand(ResetSettingsAsync, null, ReportCommandFailure);
        CopyTranscriptCommand = new RelayCommand(CopyTranscript, () => !string.IsNullOrWhiteSpace(TranscriptText));
        ClearTranscriptCommand = new RelayCommand(ClearTranscript, () => !string.IsNullOrWhiteSpace(TranscriptText));
        OpenModelsFolderCommand = new RelayCommand(() => OpenFolder(_paths.ModelsDirectory));
        OpenLogsFolderCommand = new RelayCommand(() => OpenFolder(_paths.LogsDirectory));
        RunDiagnosticsCommand = new AsyncRelayCommand(
            RunDiagnosticsAsync,
            () => !IsRunningDiagnostics,
            ReportCommandFailure);
        CopyDiagnosticsCommand = new RelayCommand(
            CopyDiagnostics,
            () => !string.IsNullOrWhiteSpace(DiagnosticsText));
        OpenDiagnosticLinkCommand = new ParameterRelayCommand(
            OpenDiagnosticLink,
            parameter => parameter is string value &&
                Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
                uri.Scheme == Uri.UriSchemeHttps);
        NavigateToModelsCommand = new RelayCommand(() => SelectedPageIndex = 2);
        PreviewOverlayCommand = new RelayCommand(ToggleOverlayPreview);

        _coordinator.StateChanged += OnStateChanged;
        _coordinator.TranscriptUpdated += OnTranscriptUpdated;
        _audioCapture.AudioLevelChanged += OnAudioLevelChanged;
        _modelManager.ModelChanged += OnModelChanged;
        ModelsView = CollectionViewSource.GetDefaultView(Models);
        ModelsView.Filter = FilterModel;
    }

    public ObservableCollection<AudioDevice> AudioDevices { get; } = [];

    public ObservableCollection<ModelInformation> Models { get; } = [];

    public ICollectionView ModelsView { get; }

    public ObservableCollection<CaptionSegment> CaptionSegments { get; } = [];

    public ObservableCollection<ComputeDiagnosticItem> ComputeDiagnostics { get; } = [];

    public AsyncRelayCommand StartStopCommand { get; }

    public AsyncRelayCommand RefreshDevicesCommand { get; }

    public AsyncRelayCommand TestAudioCommand { get; }

    public AsyncRelayCommand DownloadModelCommand { get; }

    public AsyncRelayCommand UseModelCommand { get; }

    public AsyncRelayCommand SetDefaultModelCommand { get; }

    public RelayCommand CancelDownloadCommand { get; }

    public AsyncRelayCommand DeleteModelCommand { get; }

    public AsyncRelayCommand RetryModelCommand { get; }

    public AsyncRelayCommand ResetSettingsCommand { get; }

    public RelayCommand CopyTranscriptCommand { get; }

    public RelayCommand ClearTranscriptCommand { get; }

    public RelayCommand OpenModelsFolderCommand { get; }

    public RelayCommand OpenLogsFolderCommand { get; }

    public AsyncRelayCommand RunDiagnosticsCommand { get; }

    public RelayCommand CopyDiagnosticsCommand { get; }

    public ParameterRelayCommand OpenDiagnosticLinkCommand { get; }

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
                OnPropertyChanged(nameof(LiveHeadline));
                OnPropertyChanged(nameof(LiveDescription));
                OnPropertyChanged(nameof(StartStopText));
                OnPropertyChanged(nameof(StartStopGlyph));
            }
        }
    }

    public string StatusLabel => CurrentPresentation.Label;

    public string LiveHeadline => CurrentPresentation.Headline;

    public string LiveDescription => CurrentPresentation.Description;

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

    public string StartStopText => CurrentPresentation.ButtonLabel;

    public string StartStopGlyph => CurrentPresentation.ButtonGlyph;

    public string ActiveRuntimeLabel =>
        _performanceSnapshot?.Provider switch
        {
            "cuda" or "cuda-active" => "CUDA Active",
            "cuda-ready" => "CUDA ready · awaiting decode",
            "cpu" => "CPU Active",
            { Length: > 0 } provider => provider,
            _ => "Runtime not started",
        };

    public string ActiveLanguageLabel =>
        ActiveModel?.Descriptor is { Languages.Count: 1 } descriptor
            ? descriptor.Languages[0]
            : _settings.Current.DefaultLanguage;

    public string TargetApplicationLabel => IsListening
        ? IsTextInjectionEnabled
            ? "Focused external app · exact HWND checked per commit"
            : "Typing disabled"
        : "Typing paused";

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
                OnPropertyChanged(nameof(TargetApplicationLabel));
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

    public TextInjectionDeliveryMode TextInjectionDeliveryMode
    {
        get => _textInjectionDeliveryMode;
        set
        {
            if (SetProperty(ref _textInjectionDeliveryMode, value))
            {
                _settings.Current.TextInjectionDeliveryMode = value;
                ActionMessage = value switch
                {
                    TextInjectionDeliveryMode.Automatic =>
                        "Automatic uses the compatibility profile for modern Notepad.",
                    TextInjectionDeliveryMode.Compatibility =>
                        "Compatibility uses measured single-unit paired Unicode pacing for modern Notepad.",
                    _ => "Direct uses unpaced Unicode blocks.",
                };
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
                OnPropertyChanged(nameof(CaptionLineHeight));
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

    public double CaptionHeight
    {
        get => _captionHeight;
        set
        {
            if (SetProperty(ref _captionHeight, Math.Clamp(value, 120, 720)))
            {
                _settings.Current.CaptionHeight = _captionHeight;
                PersistSettings();
            }
        }
    }

    public double? CaptionLeft
    {
        get => _captionLeft;
        private set
        {
            if (SetProperty(ref _captionLeft, value))
            {
                _settings.Current.CaptionLeft = value;
                PersistSettings();
            }
        }
    }

    public double? CaptionTop
    {
        get => _captionTop;
        private set
        {
            if (SetProperty(ref _captionTop, value))
            {
                _settings.Current.CaptionTop = value;
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

    public bool CaptionPositionLocked
    {
        get => _captionPositionLocked;
        set
        {
            if (SetProperty(ref _captionPositionLocked, value))
            {
                _settings.Current.CaptionPositionLocked = value;
                PersistSettings();
            }
        }
    }

    public int CaptionMaximumLines
    {
        get => _captionMaximumLines;
        set
        {
            if (SetProperty(ref _captionMaximumLines, Math.Clamp(value, 2, 8)))
            {
                _settings.Current.CaptionMaximumLines = _captionMaximumLines;
                RefreshCaptionCollection();
                PersistSettings();
            }
        }
    }

    public double CaptionLineSpacing
    {
        get => _captionLineSpacing;
        set
        {
            if (SetProperty(ref _captionLineSpacing, Math.Clamp(value, 1, 1.8)))
            {
                _settings.Current.CaptionLineSpacing = _captionLineSpacing;
                OnPropertyChanged(nameof(CaptionLineHeight));
                PersistSettings();
            }
        }
    }

    public double CaptionLineHeight => CaptionFontSize * CaptionLineSpacing;

    public bool CaptionShowStatusIndicator
    {
        get => _captionShowStatusIndicator;
        set
        {
            if (SetProperty(ref _captionShowStatusIndicator, value))
            {
                _settings.Current.CaptionShowStatusIndicator = value;
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

    public CaptionSegment? CurrentCaption
    {
        get => _currentCaption;
        private set
        {
            if (SetProperty(ref _currentCaption, value))
            {
                OnPropertyChanged(nameof(CurrentCaptionText));
                OnPropertyChanged(nameof(HasCurrentCaption));
            }
        }
    }

    public string CurrentCaptionText => IsOverlayPreviewing
        ? "Pending speech is replaced as recognition becomes more certain."
        : CaptionShowStableTextOnly
            ? string.Empty
            : CurrentCaption?.Text ?? string.Empty;

    public bool HasCurrentCaption => !string.IsNullOrWhiteSpace(CurrentCaptionText);

    public void UpdateCaptionBounds(double left, double top, double width, double height)
    {
        CaptionLeft = left;
        CaptionTop = top;
        CaptionWidth = width;
        CaptionHeight = height;
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
                OnPropertyChanged(nameof(CurrentCaptionText));
                OnPropertyChanged(nameof(HasCurrentCaption));
            }
        }
    }

    public ComputeBackend ComputeBackend
    {
        get => _computeBackend;
        set
        {
            if (SetProperty(ref _computeBackend, value))
            {
                _settings.Current.ComputeBackend = value;
                _settings.Current.DefaultBackend = value;
                ActionMessage = "Compute changes take effect when the model is reloaded.";
                PersistSettings();
                _ = RefreshComputeStatusAsync();
            }
        }
    }

    public RecognitionMode RecognitionMode
    {
        get => _recognitionMode;
        set
        {
            if (SetProperty(ref _recognitionMode, value))
            {
                _settings.Current.RecognitionMode = value;
                ActionMessage = "Recognition profile changes take effect when the model is reloaded.";
                PersistSettings();
            }
        }
    }

    public string DefaultLanguage
    {
        get => _settings.Current.DefaultLanguage;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value)
                ? "auto"
                : value.Trim().ToLowerInvariant();
            if (string.Equals(
                    _settings.Current.DefaultLanguage,
                    normalized,
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _settings.Current.DefaultLanguage = normalized;
            _settings.Current.Language = normalized;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ActiveLanguageLabel));
            ActionMessage = "Language changes take effect when the model is reloaded.";
            PersistSettings();
        }
    }

    public IReadOnlyList<ComputeBackend> ComputeBackends { get; } =
        Enum.GetValues<ComputeBackend>();

    public IReadOnlyList<RecognitionMode> RecognitionModes { get; } =
        Enum.GetValues<RecognitionMode>();

    public IReadOnlyList<TextInjectionDeliveryMode> TextInjectionDeliveryModes { get; } =
        Enum.GetValues<TextInjectionDeliveryMode>();

    public IReadOnlyList<string> RecognitionLanguages { get; } =
    [
        "auto", "en", "zh", "yue", "ja", "ko", "de", "es", "fr", "it",
        "pt", "ru", "ar", "hi", "th", "vi", "tr", "pl", "nl", "uk",
    ];

    public string ComputeStatus
    {
        get => _computeStatus;
        private set => SetProperty(ref _computeStatus, value);
    }

    public string DiagnosticsText
    {
        get => _diagnosticsText;
        private set
        {
            if (SetProperty(ref _diagnosticsText, value))
            {
                CopyDiagnosticsCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsRunningDiagnostics
    {
        get => _isRunningDiagnostics;
        private set
        {
            if (SetProperty(ref _isRunningDiagnostics, value))
            {
                RunDiagnosticsCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public PerformanceSnapshot? PerformanceSnapshot
    {
        get => _performanceSnapshot;
        private set
        {
            if (SetProperty(ref _performanceSnapshot, value))
            {
                OnPropertyChanged(nameof(PerformanceSummary));
                OnPropertyChanged(nameof(ActiveRuntimeLabel));
            }
        }
    }

    public string PerformanceSummary => PerformanceSnapshot is null
        ? "Waiting for runtime telemetry…"
        : $"{PerformanceSnapshot.ProcessCpuPercent:N1}% CPU · " +
          $"{PerformanceSnapshot.RealtimeFactor:N2} RTF · " +
          $"{PerformanceSnapshot.AudioQueueDurationMs:N0} ms queued · " +
          $"{PerformanceSnapshot.WorkingSetBytes / 1024d / 1024d:N0} MB";

    public string ToggleListeningHotkey
    {
        get => _toggleListeningHotkey;
        set => SetProperty(ref _toggleListeningHotkey, value);
    }

    public string ToggleInjectionHotkey
    {
        get => _toggleInjectionHotkey;
        set => SetProperty(ref _toggleInjectionHotkey, value);
    }

    public string ToggleCaptionsHotkey
    {
        get => _toggleCaptionsHotkey;
        set => SetProperty(ref _toggleCaptionsHotkey, value);
    }

    public string ToggleListeningHotkeyError
    {
        get => _toggleListeningHotkeyError;
        private set => SetProperty(ref _toggleListeningHotkeyError, value);
    }

    public string ToggleInjectionHotkeyError
    {
        get => _toggleInjectionHotkeyError;
        private set => SetProperty(ref _toggleInjectionHotkeyError, value);
    }

    public string ToggleCaptionsHotkeyError
    {
        get => _toggleCaptionsHotkeyError;
        private set => SetProperty(ref _toggleCaptionsHotkeyError, value);
    }

    public ModelInformation? SelectedModel
    {
        get => _selectedModel;
        set
        {
            if (SetProperty(ref _selectedModel, value))
            {
                RaiseModelProperties();
            }
        }
    }

    public string ModelSearchText
    {
        get => _modelSearchText;
        set
        {
            if (SetProperty(ref _modelSearchText, value))
            {
                ModelsView.Refresh();
            }
        }
    }

    public string SelectedModelFilter
    {
        get => _selectedModelFilter;
        set
        {
            if (SetProperty(ref _selectedModelFilter, value))
            {
                ModelsView.Refresh();
            }
        }
    }

    public IReadOnlyList<string> ModelFilters { get; } =
    [
        "All",
        "Installed",
        "Streaming",
        "Multilingual",
        "CUDA-capable",
    ];

    public bool IsModelReady => SelectedModel?.IsInstalled == true && SelectedModel.Availability == ModelAvailability.Ready;

    public bool CanDownloadSelectedModel =>
        SelectedModel?.Descriptor?.IntegrationStatus is
            ModelIntegrationStatus.Available or ModelIntegrationStatus.Preview &&
        !IsModelReady &&
        !IsDownloadingModel;

    public bool CanUseSelectedModel =>
        IsModelReady &&
        SelectedModel?.IsActive != true &&
        !IsDownloadingModel;

    public bool CanSetSelectedModelDefault =>
        IsModelReady &&
        SelectedModel is not null &&
        !string.Equals(
            SelectedModel.Id,
            _settings.Current.DefaultModelId,
            StringComparison.OrdinalIgnoreCase) &&
        !IsDownloadingModel;

    public bool IsSelectedModelDefault =>
        SelectedModel is not null &&
        string.Equals(
            SelectedModel.Id,
            _settings.Current.DefaultModelId,
            StringComparison.OrdinalIgnoreCase);

    public string ModelIntegrationText =>
        SelectedModel?.Descriptor?.IntegrationStatus switch
        {
            ModelIntegrationStatus.Available => SelectedModel.IsActive
                ? "Active"
                : IsModelReady
                    ? "Installed"
                    : "Available",
            ModelIntegrationStatus.Preview => SelectedModel.IsActive
                ? "Active preview"
                : IsModelReady
                    ? "Installed preview"
                    : "Preview · validation evidence incomplete",
            ModelIntegrationStatus.Experimental => "Experimental",
            ModelIntegrationStatus.ComingLater => "Coming later",
            _ => "Unknown",
        };

    public string ModelLicenseText => SelectedModel?.Descriptor?.License ?? "—";

    public string ModelLanguagesText => SelectedModel?.Descriptor?.LanguageDescription ?? "—";

    public string ModelBackendText => SelectedModel?.Descriptor is not { } descriptor
        ? "—"
        : descriptor.CudaSupported
            ? "CPU · CUDA-capable model"
            : "CPU";

    public ModelInformation? ActiveModel => Models.FirstOrDefault(model =>
        model.IsActive &&
        model.IsInstalled &&
        model.Availability == ModelAvailability.Ready);

    public bool ShowOnboarding => ActiveModel is null;

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

    public string ModelStatusText => ActiveModel?.Availability switch
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

    public string ModelStatusGlyph => ActiveModel?.Availability == ModelAvailability.Ready
        ? "\uE73E"
        : "\uE946";

    public bool CanDeleteModel => IsModelReady && !IsDownloadingModel;

    public async Task InitializeAsync()
    {
        await _settings.LoadAsync().ConfigureAwait(true);
        ApplySettings(_settings.Current);
        RefreshModels();
        await RefreshAudioDevicesAsync().ConfigureAwait(true);
        await RefreshComputeStatusAsync().ConfigureAwait(true);
        _isInitializing = false;
        _telemetryTimer.Start();

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

    public void ConfirmHotkeyRegistration(RsttHotkey hotkey, string gesture)
    {
        switch (hotkey)
        {
            case RsttHotkey.ToggleListening:
                _settings.Current.ToggleListeningHotkey = gesture;
                ToggleListeningHotkeyError = string.Empty;
                break;
            case RsttHotkey.ToggleTextInjection:
                _settings.Current.ToggleInjectionHotkey = gesture;
                ToggleInjectionHotkeyError = string.Empty;
                break;
            case RsttHotkey.ToggleCaptionOverlay:
                _settings.Current.ToggleCaptionsHotkey = gesture;
                ToggleCaptionsHotkeyError = string.Empty;
                break;
        }

        ActionMessage = $"{gesture} is registered globally.";
        PersistSettings();
    }

    public void RejectHotkeyRegistration(
        RsttHotkey hotkey,
        string workingGesture,
        string error)
    {
        switch (hotkey)
        {
            case RsttHotkey.ToggleListening:
                _toggleListeningHotkey = workingGesture;
                ToggleListeningHotkeyError = error;
                OnPropertyChanged(nameof(ToggleListeningHotkey));
                break;
            case RsttHotkey.ToggleTextInjection:
                _toggleInjectionHotkey = workingGesture;
                ToggleInjectionHotkeyError = error;
                OnPropertyChanged(nameof(ToggleInjectionHotkey));
                break;
            case RsttHotkey.ToggleCaptionOverlay:
                _toggleCaptionsHotkey = workingGesture;
                ToggleCaptionsHotkeyError = error;
                OnPropertyChanged(nameof(ToggleCaptionsHotkey));
                break;
        }

        ActionMessage = error;
    }

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

    private async Task RefreshComputeStatusAsync()
    {
        try
        {
            var probes = await _computeDevices.ProbeAsync().ConfigureAwait(true);
            var cpu = probes.FirstOrDefault(probe => probe.Backend == ComputeBackend.Cpu);
            var cuda = probes.FirstOrDefault(probe => probe.Backend == ComputeBackend.Cuda);
            ComputeStatus = ComputeBackend switch
            {
                ComputeBackend.Cpu => cpu?.Status ?? "CPU inference selected.",
                ComputeBackend.Cuda when cuda?.IsAvailable == true => cuda.Status,
                ComputeBackend.Cuda => $"CUDA unavailable; RSTT will fall back to CPU. {cuda?.Status}",
                _ when cuda?.IsAvailable == true => $"Auto will use CUDA. {cuda.Status}",
                _ => $"Auto will use CPU. {cuda?.Status ?? cpu?.Status}",
            };
        }
        catch (Exception exception)
        {
            ComputeStatus = $"Compute probe failed; CPU fallback remains available. {exception.Message}";
        }
    }

    private async Task RunDiagnosticsAsync()
    {
        IsRunningDiagnostics = true;
        try
        {
            var model = ActiveModel ?? SelectedModel;
            var compute = await _computeDevices
                .GetDiagnosticsAsync(
                    ComputeBackend,
                    model?.Descriptor)
                .ConfigureAwait(true);
            var devices = await _audioCapture.GetOutputDevicesAsync().ConfigureAwait(true);
            var performance = _performance.GetSnapshot();
            var modelValid = _modelManager.TryValidateModel(out var validatedModel);
            var selfTest = await RunSpeechSelfTestAsync(
                    model?.Descriptor,
                    modelValid)
                .ConfigureAwait(true);
            var builder = new StringBuilder();
            ComputeDiagnostics.Clear();
            foreach (var layer in compute.Layers)
            {
                ComputeDiagnostics.Add(ComputeDiagnosticItem.FromLayer(layer));
            }
            void AppendInvariant(FormattableString line) =>
                builder.AppendLine(line.ToString(CultureInfo.InvariantCulture));
            builder.AppendLine("RSTT Diagnostics");
            AppendInvariant($"Generated: {DateTimeOffset.Now:O}");
            builder.AppendLine();
            builder.AppendLine("[System]");
            AppendInvariant($"App: {Assembly.GetExecutingAssembly().GetName().Version}");
            AppendInvariant($"OS: {Environment.OSVersion.VersionString}");
            AppendInvariant($"Runtime: {Environment.Version}");
            AppendInvariant($"Process: {(Environment.Is64BitProcess ? "x64" : "x86")}");
            AppendInvariant($"Logical processors: {Environment.ProcessorCount}");
            builder.AppendLine();
            builder.AppendLine("[Graphics and CUDA]");
            foreach (var layer in compute.Layers)
            {
                AppendInvariant(
                    $"{layer.Layer}: {(layer.IsReady ? "Ready" : "Unavailable")} - {layer.Status}");
            }

            AppendInvariant($"Selected backend: {compute.SelectedBackend}");
            AppendInvariant($"Active label: {ActiveRuntimeLabel}");
            builder.AppendLine();
            builder.AppendLine("[ASR]");
            AppendInvariant($"Model: {model?.DisplayName ?? "None"}");
            AppendInvariant($"Model ID: {model?.Id ?? "None"}");
            AppendInvariant($"Revision: {model?.Descriptor?.Revision ?? "Unknown"}");
            AppendInvariant($"Mode: {model?.Descriptor?.StreamingMode.ToString() ?? "Unknown"}");
            AppendInvariant($"Language: {ActiveLanguageLabel}");
            AppendInvariant($"Installation valid: {modelValid}");
            AppendInvariant($"Validation status: {validatedModel.StatusMessage ?? "None"}");
            AppendInvariant($"Self-test: {selfTest}");
            builder.AppendLine();
            builder.AppendLine("[Audio]");
            AppendInvariant($"Selected: {SelectedAudioDeviceName}");
            AppendInvariant($"Detected output devices: {devices.Count}");
            AppendInvariant($"Capture active: {_audioCapture.IsCapturing}");
            builder.AppendLine();
            builder.AppendLine("[Performance]");
            AppendInvariant($"Provider: {performance.Provider}");
            AppendInvariant($"CPU: {performance.ProcessCpuPercent:N2}%");
            AppendInvariant($"RTF: {performance.RealtimeFactor:N3}");
            AppendInvariant($"Decode P50/P95/max: {performance.DecodeP50Ms:N1}/{performance.DecodeP95Ms:N1}/{performance.DecodeMaxMs:N1} ms");
            AppendInvariant($"Audio queue: {performance.AudioQueueDurationMs:N1} ms");
            AppendInvariant($"Injection rate: {performance.InjectionHz:N2}/s");
            AppendInvariant($"Average injection length: {performance.AverageInjectionUtf16Length:N1} UTF-16 units");
            AppendInvariant($"SendInput calls: {performance.SendInputCallsPerSecond:N2}/s");
            AppendInvariant($"Injection queue high-watermark: {performance.InjectionQueueHighWatermark}");
            AppendInvariant($"Injection failures: {performance.InjectionFailures}");
            AppendInvariant($"Working set: {performance.WorkingSetBytes / 1024d / 1024d:N1} MiB");
            builder.AppendLine();
            builder.AppendLine("Transcript content: excluded");
            DiagnosticsText = builder.ToString();
            ActionMessage = "Diagnostics self-test completed without including transcript content.";
        }
        finally
        {
            IsRunningDiagnostics = false;
        }
    }

    private async Task<string> RunSpeechSelfTestAsync(
        ModelDescriptor? descriptor,
        bool modelValid)
    {
        if (IsListening)
        {
            return "Not tested while a live session is active.";
        }

        if (!modelValid || descriptor is null)
        {
            return "Not tested because the selected model installation is not valid.";
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var generation = new SessionGenerationId(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var finalResults = 0;
        try
        {
            await using var engine = _speechEngineFactory.Create(descriptor);
            engine.RecognitionResultAvailable += OnSelfTestResult;
            await engine.InitializeAsync(timeout.Token).ConfigureAwait(true);
            await engine.StartAsync(timeout.Token).ConfigureAwait(true);
            await engine.ProcessAudioAsync(
                    new AudioChunk(
                        1,
                        DateTimeOffset.UtcNow,
                        new float[AudioChunk.SampleRate],
                        generation),
                    timeout.Token)
                .ConfigureAwait(true);
            await engine.StopAsync(timeout.Token).ConfigureAwait(true);
            engine.RecognitionResultAvailable -= OnSelfTestResult;
            return descriptor.Engine == "whisper-cpp"
                ? $"Ready. Isolated worker handshake, model load, warmup, and decode completed; {finalResults} final result(s) from silent test audio."
                : $"Ready. Recognizer load and stop/finalization lifecycle completed; {finalResults} final result(s) from silent test audio.";
        }
        catch (OperationCanceledException)
        {
            return "Failed: the isolated self-test exceeded 45 seconds.";
        }
        catch (Exception exception)
        {
            return $"Failed: {exception.Message}";
        }

        void OnSelfTestResult(object? sender, RecognitionHypothesis hypothesis)
        {
            if (hypothesis.IsFinal)
            {
                finalResults++;
            }
        }
    }

    private void CopyDiagnostics()
    {
        System.Windows.Clipboard.SetText(DiagnosticsText);
        ActionMessage = "Diagnostics copied without transcript content.";
    }

    private static void OpenDiagnosticLink(object? parameter)
    {
        if (parameter is not string value ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps)
        {
            return;
        }

        Process.Start(new ProcessStartInfo(uri.AbsoluteUri)
        {
            UseShellExecute = true,
        });
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
            var rate = update.BytesPerSecond <= 0
                ? string.Empty
                : $" · {update.BytesPerSecond / 1024d / 1024d:N1} MB/s";
            var eta = update.Eta is null
                ? string.Empty
                : $" · {FormatDuration(update.Eta.Value)} left";
            var file = update.TotalFiles <= 0
                ? string.Empty
                : $" · file {update.CurrentFileIndex}/{update.TotalFiles}";
            DownloadProgressText = update.TotalBytes <= 0
                ? update.Message
                : $"{update.Message}{file} · {update.BytesReceived / 1024d / 1024d:N0} / " +
                  $"{update.TotalBytes / 1024d / 1024d:N0} MB{rate}{eta}";
        });

        try
        {
            await _modelManager.DownloadAsync(SelectedModel.Id, progress, _modelDownloadCancellation.Token).ConfigureAwait(true);
            await _modelManager.SelectAsync(SelectedModel.Id, _modelDownloadCancellation.Token).ConfigureAwait(true);
            RefreshModels();
            DownloadProgress = 100;
            DownloadProgressText = "Verified and installed";
            await _coordinator.ReloadModelAsync().ConfigureAwait(true);
            ActionMessage = $"{SelectedModel.DisplayName} is verified, active, and ready.";
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

    private async Task UseSelectedModelAsync()
    {
        if (SelectedModel is null || !CanUseSelectedModel)
        {
            return;
        }

        var modelName = SelectedModel.DisplayName;
        var modelId = SelectedModel.Id;
        var restart = IsListening;
        if (restart)
        {
            var choice = System.Windows.MessageBox.Show(
                $"Switch to {modelName}? RSTT will finish the current words, load and warm the model, then resume listening.",
                "Switch recognition model",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Information);
            if (choice != MessageBoxResult.OK)
            {
                return;
            }

            await _coordinator.StopAsync().ConfigureAwait(true);
        }

        await _modelManager.SelectAsync(modelId).ConfigureAwait(true);
        await _coordinator.ReloadModelAsync().ConfigureAwait(true);
        if (restart)
        {
            await _coordinator.StartAsync().ConfigureAwait(true);
        }

        RefreshModels(modelId);
        ActionMessage = $"{modelName} is now active" +
            (restart ? " and listening resumed." : ".");
    }

    private async Task SetDefaultModelAsync()
    {
        if (SelectedModel is null || !CanSetSelectedModelDefault)
        {
            return;
        }

        var modelName = SelectedModel.DisplayName;
        await _modelManager.SetDefaultAsync(SelectedModel.Id).ConfigureAwait(true);
        RefreshModels(SelectedModel.Id);
        ActionMessage = $"{modelName} will be used by default on the next launch.";
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

        var deletingActiveModel = SelectedModel.IsActive;
        if (deletingActiveModel)
        {
            await _coordinator.UnloadModelAsync().ConfigureAwait(true);
        }

        await _modelManager.DeleteAsync(SelectedModel.Id).ConfigureAwait(true);
        RefreshModels();
        if (deletingActiveModel)
        {
            var fallback = Models.FirstOrDefault(model =>
                model.IsInstalled &&
                model.Availability == ModelAvailability.Ready);
            if (fallback is not null)
            {
                await _modelManager.SelectAsync(fallback.Id).ConfigureAwait(true);
                await _coordinator.ReloadModelAsync().ConfigureAwait(true);
                RefreshModels(fallback.Id);
            }
        }

        SelectedPageIndex = 2;
        ActionMessage = deletingActiveModel && Models.Any(model => model.IsActive)
            ? "The model was deleted and RSTT switched to another installed model."
            : "The local model was deleted. Install it again whenever you need it.";
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
        _captionHistory.Reset();
        CaptionSegments.Clear();
        CurrentCaption = null;
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
        _textInjectionDeliveryMode = settings.TextInjectionDeliveryMode;
        _isCaptionOverlayEnabled = settings.CaptionOverlayEnabled;
        _minimizeToTray = settings.MinimizeToTray;
        _startMinimized = settings.StartMinimized;
        _autoStartListening = settings.AutoStartListening;
        _captionFontSize = Math.Clamp(settings.CaptionFontSize, 18, 48);
        _captionOpacity = Math.Clamp(settings.CaptionOpacity, 0.45, 1);
        _captionWidth = Math.Clamp(settings.CaptionWidth, 520, 1_400);
        _captionHeight = Math.Clamp(settings.CaptionHeight, 120, 720);
        _captionLeft = settings.CaptionLeft;
        _captionTop = settings.CaptionTop;
        _captionAlwaysOnTop = settings.CaptionAlwaysOnTop;
        _captionPositionLocked = settings.CaptionPositionLocked;
        _captionMaximumLines = Math.Clamp(settings.CaptionMaximumLines, 2, 8);
        _captionLineSpacing = Math.Clamp(settings.CaptionLineSpacing, 1, 1.8);
        _captionShowStatusIndicator = settings.CaptionShowStatusIndicator;
        _captionShowStableTextOnly = settings.CaptionShowStableTextOnly;
        _computeBackend = settings.DefaultBackend;
        _recognitionMode = settings.RecognitionMode;
        _toggleListeningHotkey = settings.ToggleListeningHotkey;
        _toggleInjectionHotkey = settings.ToggleInjectionHotkey;
        _toggleCaptionsHotkey = settings.ToggleCaptionsHotkey;
        OnPropertyChanged(string.Empty);
    }

    private static void CopySettings(AppSettings source, AppSettings destination)
    {
        destination.AudioDeviceId = source.AudioDeviceId;
        destination.SpeechEngine = source.SpeechEngine;
        destination.SpeechModel = source.SpeechModel;
        destination.Language = source.Language;
        destination.ComputeBackend = source.ComputeBackend;
        destination.DefaultModelId = source.DefaultModelId;
        destination.DefaultProfileId = source.DefaultProfileId;
        destination.DefaultLanguage = source.DefaultLanguage;
        destination.DefaultBackend = source.DefaultBackend;
        destination.RecognitionMode = source.RecognitionMode;
        destination.CpuThreadLimit = source.CpuThreadLimit;
        destination.TextInjectionEnabled = source.TextInjectionEnabled;
        destination.TextInjectionDeliveryMode = source.TextInjectionDeliveryMode;
        destination.CaptionOverlayEnabled = source.CaptionOverlayEnabled;
        destination.StartMinimized = source.StartMinimized;
        destination.MinimizeToTray = source.MinimizeToTray;
        destination.AutoStartListening = source.AutoStartListening;
        destination.CaptionFontSize = source.CaptionFontSize;
        destination.CaptionOpacity = source.CaptionOpacity;
        destination.CaptionWidth = source.CaptionWidth;
        destination.CaptionHeight = source.CaptionHeight;
        destination.CaptionLeft = source.CaptionLeft;
        destination.CaptionTop = source.CaptionTop;
        destination.CaptionAlwaysOnTop = source.CaptionAlwaysOnTop;
        destination.CaptionPositionLocked = source.CaptionPositionLocked;
        destination.CaptionMaximumLines = source.CaptionMaximumLines;
        destination.CaptionLineSpacing = source.CaptionLineSpacing;
        destination.CaptionShowStatusIndicator = source.CaptionShowStatusIndicator;
        destination.CaptionShowStableTextOnly = source.CaptionShowStableTextOnly;
        destination.ToggleListeningHotkey = source.ToggleListeningHotkey;
        destination.ToggleInjectionHotkey = source.ToggleInjectionHotkey;
        destination.ToggleCaptionsHotkey = source.ToggleCaptionsHotkey;
    }

    private void RefreshModels(string? preferredModelId = null)
    {
        var selectedId = preferredModelId ?? SelectedModel?.Id ?? _settings.Current.DefaultModelId;
        Models.Clear();
        foreach (var model in _modelManager.GetAvailableModels())
        {
            Models.Add(model);
        }
        ModelsView.Refresh();

        SelectedModel = Models.FirstOrDefault(model => string.Equals(model.Id, selectedId, StringComparison.OrdinalIgnoreCase))
            ?? Models.FirstOrDefault(model => model.IsActive)
            ?? Models.FirstOrDefault();
    }

    private bool FilterModel(object item)
    {
        if (item is not ModelInformation model)
        {
            return false;
        }

        var searchMatches = string.IsNullOrWhiteSpace(ModelSearchText) ||
            model.DisplayName.Contains(ModelSearchText, StringComparison.CurrentCultureIgnoreCase) ||
            model.Description.Contains(ModelSearchText, StringComparison.CurrentCultureIgnoreCase) ||
            (model.Descriptor?.Family.Contains(
                ModelSearchText,
                StringComparison.CurrentCultureIgnoreCase) ?? false) ||
            (model.Descriptor?.LanguageDescription.Contains(
                ModelSearchText,
                StringComparison.CurrentCultureIgnoreCase) ?? false);
        if (!searchMatches)
        {
            return false;
        }

        return SelectedModelFilter switch
        {
            "Installed" => model.IsInstalled,
            "Streaming" => model.Descriptor?.StreamingMode is
                SpeechStreamingMode.NativeStreaming or
                SpeechStreamingMode.BufferedStreaming,
            "Multilingual" => (model.Descriptor?.Languages.Count ?? 0) > 2,
            "CUDA-capable" => model.Descriptor?.CudaSupported == true,
            _ => true,
        };
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
            var captions = _captionHistory.Snapshot();
            RefreshCaptionCollection(captions);
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
        OnPropertyChanged(nameof(TargetApplicationLabel));
        OnPropertyChanged(nameof(LiveHeadline));
        OnPropertyChanged(nameof(LiveDescription));
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
        OnPropertyChanged(nameof(ActiveModel));
        OnPropertyChanged(nameof(ShowOnboarding));
        OnPropertyChanged(nameof(ModelSizeText));
        OnPropertyChanged(nameof(ModelStatusText));
        OnPropertyChanged(nameof(ModelStatusGlyph));
        OnPropertyChanged(nameof(CanDeleteModel));
        OnPropertyChanged(nameof(CanDownloadSelectedModel));
        OnPropertyChanged(nameof(CanUseSelectedModel));
        OnPropertyChanged(nameof(ModelIntegrationText));
        OnPropertyChanged(nameof(ModelLicenseText));
        OnPropertyChanged(nameof(ModelLanguagesText));
        OnPropertyChanged(nameof(ModelBackendText));
        OnPropertyChanged(nameof(CanSetSelectedModelDefault));
        OnPropertyChanged(nameof(IsSelectedModelDefault));
        OnPropertyChanged(nameof(ActiveLanguageLabel));
        DownloadModelCommand.RaiseCanExecuteChanged();
        UseModelCommand.RaiseCanExecuteChanged();
        SetDefaultModelCommand.RaiseCanExecuteChanged();
        CancelDownloadCommand.RaiseCanExecuteChanged();
        DeleteModelCommand.RaiseCanExecuteChanged();
        RetryModelCommand.RaiseCanExecuteChanged();
        StartStopCommand.RaiseCanExecuteChanged();
        TestAudioCommand.RaiseCanExecuteChanged();
    }

    private void RefreshCaptionCollection(
        CaptionHistorySnapshot? snapshot = null)
    {
        var captions = snapshot ?? _captionHistory.Snapshot();
        var current = captions.CurrentPartial;
        var finalCapacity = Math.Max(
            1,
            CaptionMaximumLines - (current is null ? 0 : 1));
        CaptionSegments.Clear();
        foreach (var segment in captions.FinalSegments.TakeLast(finalCapacity))
        {
            CaptionSegments.Add(segment);
        }

        CurrentCaption = current;
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

    private void OnTelemetryTick(object? sender, EventArgs eventArgs)
    {
        try
        {
            PerformanceSnapshot = _performance.GetSnapshot();
        }
        catch (ObjectDisposedException)
        {
            _telemetryTimer.Stop();
        }
    }

    private StatePresentation CurrentPresentation =>
        CurrentState switch
        {
            ApplicationState.Listening => new(
                "Listening",
                "Listening and transcribing",
                IsTextInjectionEnabled
                    ? "Stable commits are typed into the exact focused window."
                    : "Captions are live; typing is disabled.",
                "Stop listening",
                "\uE71A"),
            ApplicationState.Ready => new(
                "Ready",
                "Ready to listen",
                "Your local model is loaded and no audio is being captured.",
                "Start listening",
                "\uE768"),
            ApplicationState.ModelLoading => new(
                "Loading model",
                "Loading and warming the model",
                "Recognition will be available after the local runtime is ready.",
                "Start listening",
                "\uE768"),
            ApplicationState.ModelDownloading => new(
                "Downloading",
                "Downloading a verified model",
                "Progress, validation, and retry state are shown on the Models page.",
                "Start listening",
                "\uE768"),
            ApplicationState.ModelMissing => new(
                "Setup needed",
                "Install a supported model",
                "Choose a production-supported local model on the Models page.",
                "Start listening",
                "\uE768"),
            ApplicationState.Stopping => new(
                "Stopping",
                "Finishing final words",
                "Audio, recognition results, commits, and typing are draining in order.",
                "Stopping…",
                "\uE71A"),
            ApplicationState.Error => new(
                "Needs attention",
                "Recognition needs attention",
                StatusText,
                "Start listening",
                "\uE768"),
            _ => new(
                "Starting",
                "Preparing local transcription",
                "RSTT is checking your audio, model, and runtime.",
                "Start listening",
                "\uE768"),
        };

    private static string FormatDuration(TimeSpan duration) =>
        duration.TotalHours >= 1
            ? $"{duration.TotalHours:N1} h"
            : duration.TotalMinutes >= 1
                ? $"{duration.TotalMinutes:N0} min"
                : $"{Math.Max(1, duration.TotalSeconds):N0} s";

    private sealed record StatePresentation(
        string Label,
        string Headline,
        string Description,
        string ButtonLabel,
        string ButtonGlyph);

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
        _telemetryTimer.Stop();
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
