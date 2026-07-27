using RSTT.Core.Models;

namespace RSTT.Core.Abstractions;

public interface ISpeechRecognitionEngine : IAsyncDisposable
{
    bool IsReady { get; }

    ModelInformation ModelInformation { get; }

    event EventHandler<RecognitionResult>? RecognitionResultAvailable;

    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task StartAsync(CancellationToken cancellationToken = default);

    Task ProcessAudioAsync(AudioChunk chunk, CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);

    Task ResetAsync(CancellationToken cancellationToken = default);
}
