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
    bool IsRecommended = false,
    long InstalledSizeBytes = 0,
    bool IsActive = false,
    ModelDescriptor? Descriptor = null)
{
    public string DownloadSizeText => DownloadSizeBytes <= 0
        ? "—"
        : $"{DownloadSizeBytes / 1024d / 1024d:N0} MB";

    public string IntegrationStatusText =>
        Descriptor?.IntegrationStatus switch
        {
            ModelIntegrationStatus.Available => IsActive
                ? "Active"
                : IsInstalled
                    ? "Installed"
                    : "Available",
            ModelIntegrationStatus.Preview => IsActive
                ? "Active preview"
                : IsInstalled
                    ? "Installed preview"
                    : "Preview",
            ModelIntegrationStatus.Experimental => "Experimental",
            ModelIntegrationStatus.ComingLater => "Coming later",
            _ => "Unknown",
        };
}
