namespace RSTT.Core.Settings;

public sealed class AppSettings
{
    public string? AudioDeviceId { get; set; }

    public string SpeechEngine { get; set; } = "SherpaOnnx";

    public string SpeechModel { get; set; } = "ParakeetUnifiedEnInt8";

    public string Language { get; set; } = "en";

    public bool TextInjectionEnabled { get; set; } = true;

    public bool CaptionOverlayEnabled { get; set; } = true;

    public bool StartMinimized { get; set; }

    public bool MinimizeToTray { get; set; } = true;

    public bool AutoStartListening { get; set; }

    public bool HasCompletedOnboarding { get; set; }

    public double CaptionFontSize { get; set; } = 28;

    public double CaptionOpacity { get; set; } = 0.9;

    public double CaptionWidth { get; set; } = 900;

    public bool CaptionAlwaysOnTop { get; set; } = true;

    public bool CaptionShowStableTextOnly { get; set; }

    public string ToggleListeningHotkey { get; set; } = "Ctrl+Alt+R";

    public string ToggleInjectionHotkey { get; set; } = "Ctrl+Alt+T";

    public string ToggleCaptionsHotkey { get; set; } = "Ctrl+Alt+C";
}
