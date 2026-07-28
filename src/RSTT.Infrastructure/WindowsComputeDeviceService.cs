using System.Runtime.InteropServices;
using System.Text;
using RSTT.Core.Abstractions;
using RSTT.Core.Compute;
using RSTT.Core.Models;

namespace RSTT.Infrastructure;

/// <summary>
/// Separates adapter presence from executable runtime capability. The standard RSTT package
/// currently contains sherpa's CPU runtime, so CUDA is never advertised as available merely
/// because an NVIDIA adapter exists.
/// </summary>
public sealed class WindowsComputeDeviceService : IComputeDeviceService
{
    private IReadOnlyList<ComputeBackendProbe>? _cached;

    public Task<IReadOnlyList<ComputeBackendProbe>> ProbeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_cached ??= Probe());
    }

    public async Task<ComputeSelectionResult> SelectAsync(
        ComputeBackend requested,
        ModelDescriptor model,
        CancellationToken cancellationToken = default)
    {
        var probes = await ProbeAsync(cancellationToken).ConfigureAwait(false);
        return ComputeSelectionPolicy.Select(requested, model, probes);
    }

    private static List<ComputeBackendProbe> Probe()
    {
        var cpu = new ComputeDeviceInfo(
            Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "Windows CPU",
            "CPU",
            0,
            0,
            string.Empty,
            [ComputeBackend.Cpu],
            false,
            true);
        var probes = new List<ComputeBackendProbe>
        {
            new(ComputeBackend.Cpu, true, "CPU inference is available.", cpu),
        };

        var adapters = EnumerateDisplayAdapters();
        var nvidia = adapters.FirstOrDefault(adapter =>
            adapter.Vendor.Equals("NVIDIA", StringComparison.OrdinalIgnoreCase));
        if (nvidia is null)
        {
            probes.Add(new ComputeBackendProbe(
                ComputeBackend.Cuda,
                false,
                "No NVIDIA display adapter was detected."));
            return probes;
        }

        probes.Add(new ComputeBackendProbe(
            ComputeBackend.Cuda,
            false,
            $"{nvidia.Name} detected, but this package contains the CPU sherpa-onnx runtime. " +
            "Install the matching verified CUDA runtime package before selecting CUDA.",
            nvidia));
        return probes;
    }

    private static List<ComputeDeviceInfo> EnumerateDisplayAdapters()
    {
        var result = new List<ComputeDeviceInfo>();
        for (uint index = 0; ; index++)
        {
            var displayDevice = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
            if (!EnumDisplayDevices(null, index, ref displayDevice, 0))
            {
                break;
            }

            if ((displayDevice.StateFlags & DisplayDeviceStateFlags.MirroringDriver) != 0 ||
                string.IsNullOrWhiteSpace(displayDevice.DeviceString))
            {
                continue;
            }

            var vendor = displayDevice.DeviceString.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)
                ? "NVIDIA"
                : displayDevice.DeviceString.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
                  displayDevice.DeviceString.Contains("Radeon", StringComparison.OrdinalIgnoreCase)
                    ? "AMD"
                    : displayDevice.DeviceString.Contains("Intel", StringComparison.OrdinalIgnoreCase)
                        ? "Intel"
                        : "Unknown";
            result.Add(new ComputeDeviceInfo(
                displayDevice.DeviceString,
                vendor,
                0,
                0,
                string.Empty,
                [],
                vendor is "NVIDIA" or "AMD",
                vendor == "Intel"));
        }

        return result;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevices(
        string? device,
        uint deviceNumber,
        ref DisplayDevice displayDevice,
        uint flags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int Size;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;

        public DisplayDeviceStateFlags StateFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceId;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }

    [Flags]
    private enum DisplayDeviceStateFlags
    {
        MirroringDriver = 0x00000008,
    }
}
