using RSTT.Core.Models;

namespace RSTT.Core.Abstractions;

/// <summary>
/// Compatibility surface for existing callers. New diagnostics use the more
/// explicit hardware/backend services.
/// </summary>
public interface IComputeDeviceService : IComputeBackendService
{
}
