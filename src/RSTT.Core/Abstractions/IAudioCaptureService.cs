using RSTT.Core.Models;

namespace RSTT.Core.Abstractions;

public interface IAudioCaptureService : IAsyncDisposable
{
    bool IsCapturing { get; }

    event EventHandler<AudioLevelEventArgs>? AudioLevelChanged;

    Task<IReadOnlyList<AudioDevice>> GetOutputDevicesAsync(CancellationToken cancellationToken = default);

    Task StartAsync(string? deviceId, CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);

    IAsyncEnumerable<AudioChunk> ReadChunksAsync(CancellationToken cancellationToken = default);
}
