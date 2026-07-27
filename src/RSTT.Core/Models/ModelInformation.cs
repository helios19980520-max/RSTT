namespace RSTT.Core.Models;

public sealed record ModelInformation(
    string Id,
    string DisplayName,
    string Directory,
    bool IsInstalled,
    string? StatusMessage = null,
    ModelAvailability Availability = ModelAvailability.NotInstalled,
    long DownloadSizeBytes = 0,
    string Description = "",
    string Latency = "",
    string Accuracy = "",
    bool IsRecommended = false);
