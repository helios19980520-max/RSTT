using Microsoft.Extensions.Logging;
using RSTT.Core.Abstractions;

namespace RSTT.Speech;

/// <summary>Native-streaming sherpa engine hosted in an isolated worker.</summary>
public sealed class SherpaOnlineEngine : SherpaWorkerRecognitionEngine
{
    public SherpaOnlineEngine(
        IModelManager models,
        ISettingsService settings,
        IComputeDeviceService compute,
        IPerformanceMonitor performance,
        ILogger<SherpaOnlineEngine> logger)
        : base(models, settings, compute, performance, logger)
    {
    }
}
