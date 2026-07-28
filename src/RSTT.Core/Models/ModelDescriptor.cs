namespace RSTT.Core.Models;

public enum ModelIntegrationStatus
{
    Available,
    Experimental,
    ComingLater,
}

public enum SpeechStreamingMode
{
    NativeStreaming,
    BufferedStreaming,
    SegmentedVad,
    Offline,
}

public enum ModelAccuracyTier
{
    NotBenchmarked,
    Good,
    VeryGood,
    Excellent,
}

public enum ModelSpeedTier
{
    NotBenchmarked,
    Lightweight,
    Fast,
    Balanced,
    Demanding,
}

public sealed class ModelArtifact
{
    public string Key { get; init; } = string.Empty;

    public string FileName { get; init; } = string.Empty;

    public string SourceUrl { get; init; } = string.Empty;

    public long ExpectedBytes { get; init; }

    public string Sha256 { get; init; } = string.Empty;

    public bool IsRequired { get; init; } = true;
}

public sealed class ModelCapabilities
{
    public bool SupportsPunctuation { get; init; }

    public bool SupportsCapitalization { get; init; }

    public bool SupportsHotwords { get; init; }

    public bool SupportsLanguageDetection { get; init; }

    public bool SupportsWordTimestamps { get; init; }

    public bool SupportsPartialResults { get; init; } = true;
}

/// <summary>A built-in, offline-available description of one curated speech model integration.</summary>
public sealed class ModelDescriptor
{
    public string Id { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string ShortName { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public string Publisher { get; init; } = string.Empty;

    public string Family { get; init; } = string.Empty;

    public string Engine { get; init; } = string.Empty;

    public string Architecture { get; init; } = string.Empty;

    public long ParameterCount { get; init; }

    public IReadOnlyList<string> Languages { get; init; } = [];

    public string LanguageDescription { get; init; } = string.Empty;

    public SpeechStreamingMode StreamingMode { get; init; }

    public IReadOnlyList<StreamingRecognitionProfile> LatencyProfiles { get; init; } = [];

    public long DownloadBytes { get; init; }

    public long InstalledBytes { get; init; }

    public string License { get; init; } = string.Empty;

    public string LicenseUrl { get; init; } = string.Empty;

    public string SourceUrl { get; init; } = string.Empty;

    public string DocumentationUrl { get; init; } = string.Empty;

    public string Revision { get; init; } = string.Empty;

    public bool CpuSupported { get; init; } = true;

    public bool CudaSupported { get; init; }

    public IReadOnlyList<string> OtherBackends { get; init; } = [];

    public long RecommendedRamBytes { get; init; }

    public long RecommendedVramBytes { get; init; }

    public ModelCapabilities Capabilities { get; init; } = new();

    public ModelAccuracyTier AccuracyTier { get; init; }

    public ModelSpeedTier SpeedTier { get; init; }

    public string RecommendedUse { get; init; } = string.Empty;

    public bool IsRecommended { get; init; }

    public ModelIntegrationStatus IntegrationStatus { get; init; }

    public int FeatureDimension { get; init; } = 80;

    public string DirectoryName { get; init; } = string.Empty;

    public IReadOnlyList<ModelArtifact> Artifacts { get; init; } = [];
}
