using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RSTT.App.Services;
using RSTT.App.ViewModels;
using RSTT.Audio;
using RSTT.Core.Abstractions;
using RSTT.Core.State;
using RSTT.Core.Transcription;
using RSTT.Infrastructure;
using RSTT.Input;
using RSTT.Speech;

namespace RSTT.App;

public partial class App : System.Windows.Application
{
    private ServiceProvider? _serviceProvider;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _serviceProvider = ConfigureServices();
        var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
        MainWindow = mainWindow;
        mainWindow.Show();
        _ = InitializeAsync(_serviceProvider.GetRequiredService<MainViewModel>(), mainWindow);
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_serviceProvider is not null)
        {
            await _serviceProvider.GetRequiredService<MainViewModel>().FlushSettingsAsync().ConfigureAwait(true);
            var coordinator = _serviceProvider.GetRequiredService<RecognitionCoordinator>();
            await coordinator.StopAsync().ConfigureAwait(true);
            await _serviceProvider.DisposeAsync().ConfigureAwait(true);
        }

        base.OnExit(e);
    }

    private static ServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAppPaths, AppPaths>();
        services.AddSingleton<ILoggerProvider, LocalFileLoggerProvider>();
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddDebug();
        });
        services.AddSingleton<ISettingsService, JsonSettingsService>();
        services.AddSingleton<IApplicationStateService, ApplicationStateService>();
        services.AddSingleton<IModelCatalog, JsonModelCatalog>();
        services.AddSingleton<IPerformanceMonitor, PerformanceMonitor>();
        services.AddSingleton<IHardwareDetectionService, WindowsHardwareDetectionService>();
        services.AddSingleton<IComputeDeviceService, WindowsComputeDeviceService>();
        services.AddSingleton<IComputeBackendService>(
            provider => provider.GetRequiredService<IComputeDeviceService>());
        services.AddSingleton<TextFormattingPolicy>();
        services.AddSingleton<ITranscriptCommitPolicy, ModelAwareTranscriptCommitPolicy>();
        services.AddSingleton<TranscriptStabilizer>();
        services.AddSingleton<CaptionHistory>();
        services.AddSingleton<IAudioCaptureService, WasapiLoopbackAudioCaptureService>();
        services.AddSingleton<IModelManager, LocalModelManager>();
        services.AddSingleton<ISpeechEngineFactory, SpeechEngineFactory>();
        services.AddSingleton<ISpeechRecognitionEngine, SpeechEngineRouter>();
        services.AddSingleton<ITextInjectionService, Win32TextInjectionService>();
        services.AddSingleton<RecognitionCoordinator>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();
        return services.BuildServiceProvider(validateScopes: true);
    }

    private static async Task InitializeAsync(MainViewModel viewModel, MainWindow mainWindow)
    {
        try
        {
            await viewModel.InitializeAsync();
            if (viewModel.StartMinimized)
            {
                mainWindow.Hide();
            }
        }
        catch (Exception exception)
        {
            System.Windows.MessageBox.Show($"RSTT could not initialize.\n\n{exception.Message}\n\nSee the local log folder for details.", "RSTT", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
