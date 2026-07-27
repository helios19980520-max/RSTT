namespace RSTT.Core.Models;

/// <summary>External model configuration kept outside the executable and speech UI.</summary>
public sealed record ModelInstallation(
    ModelInformation Information,
    string Engine,
    IReadOnlyDictionary<string, string> Files,
    int NumThreads = 2,
    string Provider = "cpu",
    int FeatureDimension = 80);
