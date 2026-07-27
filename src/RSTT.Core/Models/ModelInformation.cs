namespace RSTT.Core.Models;

public sealed record ModelInformation(
    string Id,
    string DisplayName,
    string Directory,
    bool IsInstalled,
    string? StatusMessage = null);
