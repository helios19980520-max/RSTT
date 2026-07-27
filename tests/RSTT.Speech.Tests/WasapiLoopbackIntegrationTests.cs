using Microsoft.Extensions.Logging.Abstractions;
using RSTT.Audio;
using RSTT.Core.Models;
using Xunit;

namespace RSTT.Speech.Tests;

public sealed class WasapiLoopbackIntegrationTests
{
    [Fact]
    [Trait("Category", "WindowsIntegration")]
    public async Task DefaultLoopbackDeviceStartsAndProducesNormalizedAudio()
    {
        await using var capture = new WasapiLoopbackAudioCaptureService(
            NullLogger<WasapiLoopbackAudioCaptureService>.Instance);
        var devices = await capture.GetOutputDevicesAsync();
        var device = Assert.Single(devices.Where(candidate => candidate.IsDefault).Take(1));
        var levels = new List<AudioLevelEventArgs>();
        capture.AudioLevelChanged += (_, level) => levels.Add(level);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(8));

        await capture.StartAsync(device.Id, cancellation.Token);
        System.Media.SystemSounds.Beep.Play();

        AudioChunk? firstChunk = null;
        await foreach (var chunk in capture.ReadChunksAsync(cancellation.Token))
        {
            firstChunk = chunk;
            break;
        }

        await capture.StopAsync();

        Assert.NotNull(firstChunk);
        Assert.NotEmpty(firstChunk.Samples);
        Assert.All(firstChunk.Samples, sample => Assert.InRange(sample, -1f, 1f));
        Assert.Contains(levels, level => level.Level is >= 0 and <= 1 && level.Peak is >= 0 and <= 1);
    }
}
