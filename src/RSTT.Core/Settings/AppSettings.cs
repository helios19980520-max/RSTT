using RSTT.Core.Models;

namespace RSTT.Core.Settings;

public sealed class AppSettings
{
    public string? AudioDeviceId { get; set; }

    public string SpeechEngine { get; set; } = "SherpaOnnx";

    public string SpeechModel { get; set; } = "NemotronStreamingEn06BInt8_560ms_20260425";

    public string Language { get; set; } = "en";

    public ComputeBackend ComputeBackend { get; set; } = ComputeBackend.Auto;

    public string DefaultModelId { get; set; } = "NemotronStreamingEn06BInt8_560ms_20260425";

    public string DefaultProfileId { get; set; } = "balanced-560";

    public string DefaultLanguage { get; set; } = "en";

    public ComputeBackend DefaultBackend { get; set; } = ComputeBackend.Auto;

    public RecognitionMode RecognitionMode { get; set; } = RecognitionMode.Balanced;

    public int CpuThreadLimit { get; set; }

    public bool TextInjectionEnabled { get; set; } = true;

    public TextInjectionDeliveryMode TextInjectionDeliveryMode { get; set; } =
        TextInjectionDeliveryMode.Automatic;

    public bool CaptionOverlayEnabled { get; set; } = true;

    public bool StartMinimized { get; set; }

    public bool MinimizeToTray { get; set; } = true;

    public bool AutoStartListening { get; set; }

    public bool HasCompletedOnboarding { get; set; }

    public double CaptionFontSize { get; set; } = 28;

    public double CaptionOpacity { get; set; } = 0.9;

    public double CaptionWidth { get; set; } = 900;

    public double CaptionHeight { get; set; } = 210;

    public double? CaptionLeft { get; set; }

    public double? CaptionTop { get; set; }

    public bool CaptionAlwaysOnTop { get; set; } = true;

    public bool CaptionPositionLocked { get; set; } = true;

    public int CaptionMaximumLines { get; set; } = 4;

    public double CaptionLineSpacing { get; set; } = 1.2;

    public bool CaptionShowStatusIndicator { get; set; } = true;

    public bool CaptionShowStableTextOnly { get; set; }

    public string ToggleListeningHotkey { get; set; } = "Ctrl+Alt+R";

    public string ToggleInjectionHotkey { get; set; } = "Ctrl+Alt+T";

    public string ToggleCaptionsHotkey { get; set; } = "Ctrl+Alt+C";
}

public enum TextInjectionDeliveryMode
{
    Automatic,
    Direct,
    Compatibility,
}
