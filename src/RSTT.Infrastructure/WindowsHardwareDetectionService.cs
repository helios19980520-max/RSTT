using System.Runtime.InteropServices;
using RSTT.Core.Abstractions;
using RSTT.Core.Models;

namespace RSTT.Infrastructure;

/// <summary>Enumerates physical graphics adapters through DXGI 1.1.</summary>
public sealed class WindowsHardwareDetectionService : IHardwareDetectionService
{
    private HardwareDetectionReport? _cached;

    public Task<HardwareDetectionReport> DetectAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_cached ??= Detect());
    }

    private static HardwareDetectionReport Detect()
    {
        var factoryId = typeof(IDxgiFactory1).GUID;
        var result = NativeMethods.CreateDXGIFactory1(ref factoryId, out var factory);
        Marshal.ThrowExceptionForHR(result);
        var adapters = new List<ComputeDeviceInfo>();
        try
        {
            for (uint index = 0; ; index++)
            {
                var status = factory.EnumAdapters1(index, out var adapter);
                if (status == DxgiErrorNotFound)
                {
                    break;
                }

                Marshal.ThrowExceptionForHR(status);
                try
                {
                    Marshal.ThrowExceptionForHR(adapter.GetDesc1(out var description));
                    var software = (description.Flags & DxgiAdapterFlagSoftware) != 0;
                    if (software)
                    {
                        continue;
                    }

                    var vendor = VendorName(description.VendorId);
                    var dedicated = checked((long)description.DedicatedVideoMemory);
                    var shared = checked((long)description.SharedSystemMemory);
                    adapters.Add(new ComputeDeviceInfo(
                        description.Description.TrimEnd('\0'),
                        vendor,
                        dedicated,
                        shared,
                        string.Empty,
                        vendor == "NVIDIA" ? [ComputeBackend.Cuda] : [],
                        dedicated > 0,
                        dedicated == 0 && shared > 0,
                        description.VendorId,
                        description.DeviceId,
                        software));
                }
                finally
                {
                    Marshal.FinalReleaseComObject(adapter);
                }
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject(factory);
        }

        return new HardwareDetectionReport(adapters, "DXGI 1.1", DateTimeOffset.UtcNow);
    }

    private static string VendorName(uint vendorId) =>
        vendorId switch
        {
            0x10DE => "NVIDIA",
            0x1002 or 0x1022 => "AMD",
            0x8086 => "Intel",
            0x1414 => "Microsoft",
            _ => $"PCI {vendorId:X4}",
        };

    private const int DxgiErrorNotFound = unchecked((int)0x887A0002);
    private const uint DxgiAdapterFlagSoftware = 0x2;
}

internal static partial class NativeMethods
{
    [DllImport("dxgi.dll")]
    internal static extern int CreateDXGIFactory1(
        ref Guid factoryId,
        [MarshalAs(UnmanagedType.Interface)] out IDxgiFactory1 factory);
}

[ComImport]
[Guid("770AAE78-F26F-4DBA-A829-253C83D1B387")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDxgiFactory1
{
    [PreserveSig]
    int SetPrivateData(ref Guid name, uint dataSize, nint data);

    [PreserveSig]
    int SetPrivateDataInterface(ref Guid name, nint unknown);

    [PreserveSig]
    int GetPrivateData(ref Guid name, ref uint dataSize, nint data);

    [PreserveSig]
    int GetParent(ref Guid interfaceId, out nint parent);

    [PreserveSig]
    int EnumAdapters(uint adapter, out nint adapterPointer);

    [PreserveSig]
    int MakeWindowAssociation(nint windowHandle, uint flags);

    [PreserveSig]
    int GetWindowAssociation(out nint windowHandle);

    [PreserveSig]
    int CreateSwapChain(nint device, nint description, out nint swapChain);

    [PreserveSig]
    int CreateSoftwareAdapter(nint module, out nint adapter);

    [PreserveSig]
    int EnumAdapters1(uint adapter, [MarshalAs(UnmanagedType.Interface)] out IDxgiAdapter1 adapterObject);

    [PreserveSig]
    int IsCurrent();
}

[ComImport]
[Guid("29038F61-3839-4626-91FD-086879011A05")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDxgiAdapter1
{
    [PreserveSig]
    int SetPrivateData(ref Guid name, uint dataSize, nint data);

    [PreserveSig]
    int SetPrivateDataInterface(ref Guid name, nint unknown);

    [PreserveSig]
    int GetPrivateData(ref Guid name, ref uint dataSize, nint data);

    [PreserveSig]
    int GetParent(ref Guid interfaceId, out nint parent);

    [PreserveSig]
    int EnumOutputs(uint output, out nint outputPointer);

    [PreserveSig]
    int GetDesc(out DxgiAdapterDescription description);

    [PreserveSig]
    int CheckInterfaceSupport(ref Guid interfaceId, out long userModeDriverVersion);

    [PreserveSig]
    int GetDesc1(out DxgiAdapterDescription1 description);
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct DxgiAdapterDescription
{
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
    public string Description;

    public uint VendorId;
    public uint DeviceId;
    public uint SubSystemId;
    public uint Revision;
    public nuint DedicatedVideoMemory;
    public nuint DedicatedSystemMemory;
    public nuint SharedSystemMemory;
    public long AdapterLuid;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct DxgiAdapterDescription1
{
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
    public string Description;

    public uint VendorId;
    public uint DeviceId;
    public uint SubSystemId;
    public uint Revision;
    public nuint DedicatedVideoMemory;
    public nuint DedicatedSystemMemory;
    public nuint SharedSystemMemory;
    public long AdapterLuid;
    public uint Flags;
}
