namespace RSTT.Core.Models;

public sealed record ModelDownloadProgress(
    string ModelId,
    ModelAvailability Stage,
    long BytesReceived,
    long TotalBytes,
    string CurrentFile,
    string Message)
{
    public double Fraction => TotalBytes <= 0 ? 0 : Math.Clamp((double)BytesReceived / TotalBytes, 0, 1);
}
