using Microsoft.Extensions.Logging;
using RSTT.Core.Abstractions;
using RSTT.Core.Models;

namespace RSTT.Speech;

public sealed class SpeechEngineFactory : ISpeechEngineFactory
{
    private readonly IModelManager _models;
    private readonly ISettingsService _settings;
    private readonly IComputeDeviceService _compute;
    private readonly IPerformanceMonitor _performance;
    private readonly ILoggerFactory _loggerFactory;

    public SpeechEngineFactory(
        IModelManager models,
        ISettingsService settings,
        IComputeDeviceService compute,
        IPerformanceMonitor performance,
        ILoggerFactory loggerFactory)
    {
        _models = models;
        _settings = settings;
        _compute = compute;
        _performance = performance;
        _loggerFactory = loggerFactory;
    }

    public ISpeechRecognitionEngine Create(ModelDescriptor descriptor) =>
        string.Equals(descriptor.Engine, "whisper-cpp", StringComparison.OrdinalIgnoreCase)
            ? new WhisperCppEngine(
                _models,
                _settings,
                _performance,
                _loggerFactory.CreateLogger<WhisperCppEngine>())
            : descriptor.StreamingMode is SpeechStreamingMode.SegmentedVad or
                SpeechStreamingMode.Offline
                ? new SherpaOfflineVadEngine(
                _models,
                _settings,
                _compute,
                _performance,
                _loggerFactory.CreateLogger<SherpaOfflineVadEngine>())
                : new SherpaOnlineEngine(
                _models,
                _settings,
                _compute,
                _performance,
                _loggerFactory.CreateLogger<SherpaOnlineEngine>());
}
