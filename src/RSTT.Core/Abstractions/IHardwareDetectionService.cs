using RSTT.Core.Models;

namespace RSTT.Core.Abstractions;

public interface IHardwareDetectionService
{
    Task<HardwareDetectionReport> DetectAsync(CancellationToken cancellationToken = default);
}
