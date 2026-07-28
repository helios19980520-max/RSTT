using Microsoft.Extensions.Logging;
using RSTT.Core.Abstractions;

namespace RSTT.Speech;

/// <summary>Offline/VAD sherpa engine hosted in an isolated worker.</summary>
public sealed class SherpaOfflineVadEngine : SherpaWorkerRecognitionEngine
{
    public SherpaOfflineVadEngine(
        IModelManager models,
        ISettingsService settings,
        IComputeDeviceService compute,
        IPerformanceMonitor performance,
        ILogger<SherpaOfflineVadEngine> logger)
        : base(models, settings, compute, performance, logger)
    {
    }
}
