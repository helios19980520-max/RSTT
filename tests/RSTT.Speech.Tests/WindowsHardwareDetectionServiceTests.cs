using RSTT.Infrastructure;
using Xunit;

namespace RSTT.Speech.Tests;

public sealed class WindowsHardwareDetectionServiceTests
{
    [Fact]
    [Trait("Category", "WindowsIntegration")]
    public async Task DxgiReturnsOnlyPhysicalAdaptersWithPciIdentity()
    {
        var service = new WindowsHardwareDetectionService();

        var report = await service.DetectAsync();

        Assert.Equal("DXGI 1.1", report.Source);
        Assert.NotEmpty(report.Adapters);
        Assert.All(report.Adapters, adapter =>
        {
            Assert.False(adapter.IsSoftwareAdapter);
            Assert.NotEqual(0u, adapter.VendorId);
            Assert.NotEqual(0u, adapter.DeviceId);
            Assert.False(string.IsNullOrWhiteSpace(adapter.Name));
        });
    }
}
