namespace RSTT.Core.Models;

public sealed record ModelDownloadProgress(
    string ModelId,
    ModelAvailability Stage,
    long BytesReceived,
    long TotalBytes,
    string CurrentFile,
    string Message,
    int CurrentFileIndex = 0,
    int TotalFiles = 0,
    long CurrentFileBytes = 0,
    long CurrentFileTotalBytes = 0,
    double BytesPerSecond = 0,
    TimeSpan? Eta = null)
{
    public double Fraction => TotalBytes <= 0 ? 0 : Math.Clamp((double)BytesReceived / TotalBytes, 0, 1);

    public double Percentage => Fraction * 100;

    public bool IsIndeterminate => TotalBytes <= 0;
}
