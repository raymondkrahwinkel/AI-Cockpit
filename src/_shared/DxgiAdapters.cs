using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Cockpit.Shared;

internal static class DxgiAdapters
{
    internal const string DeviceNameVariable = "COCKPIT_GPU_DEVICE_NAME";
    private const uint SoftwareAdapter = 2;
    private const int EnumAdapterByGpuPreferenceSlot = 29;
    private const uint HighPerformance = 2;

    internal sealed record Adapter(string? Name, long Luid, uint VendorId, uint DeviceId, long VideoMemoryBytes, bool DrivesDisplay);

    internal static string[] ChromiumArguments(string? deviceName)
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        var adapter = InPreferredOrder(deviceName).FirstOrDefault();
        return
        [
            "--enable-gpu",
            "--use-gl=angle",
            "--use-angle=d3d11",
            "--disable-software-rasterizer",
            adapter is null ? "--force_high_performance_gpu" :
                FormattableString.Invariant($"--use-adapter-luid={(int)(adapter.Luid >> 32)},{unchecked((uint)adapter.Luid)}"),
        ];
    }

    [SupportedOSPlatform("windows")]
    internal static IReadOnlyList<Adapter> InPreferredOrder(string? deviceName)
    {
        try
        {
            var adapters = Enumerate(preferPerformance: true);
            return string.IsNullOrWhiteSpace(deviceName)
                ? adapters
                : adapters.OrderByDescending(adapter => string.Equals(adapter.Name, deviceName, StringComparison.OrdinalIgnoreCase)).ToArray();
        }
        catch (Exception exception) when (exception is COMException or DllNotFoundException or EntryPointNotFoundException)
        {
            return [];
        }
    }

    [SupportedOSPlatform("windows")]
    internal static IReadOnlyList<Adapter> Enumerate(bool preferPerformance = false)
    {
        var iid = typeof(IDXGIFactory1).GUID;
        if (CreateDXGIFactory1(ref iid, out var factoryPtr) != 0 || factoryPtr == IntPtr.Zero)
        {
            return [];
        }

        var factory = (IDXGIFactory1)Marshal.GetObjectForIUnknown(factoryPtr);

        var preferredFactory = IntPtr.Zero;
        var candidates = new List<Adapter>();
        try
        {
            EnumAdapterByGpuPreference? enumeratePreferred = null;
            var adapterInterface = typeof(IDXGIAdapter1).GUID;
            if (preferPerformance)
            {
                var factory6Interface = new Guid("c1b6694f-ff09-44a9-b03c-77900a0a1d17");
                if (Marshal.QueryInterface(factoryPtr, in factory6Interface, out preferredFactory) == 0)
                {
                    var vtable = Marshal.ReadIntPtr(preferredFactory);
                    enumeratePreferred = Marshal.GetDelegateForFunctionPointer<EnumAdapterByGpuPreference>(
                        Marshal.ReadIntPtr(vtable, EnumAdapterByGpuPreferenceSlot * IntPtr.Size));
                }
            }

            for (uint index = 0; ; index++)
            {
                var hr = enumeratePreferred is null
                    ? factory.EnumAdapters1(index, out var adapterPtr)
                    : enumeratePreferred(preferredFactory, index, HighPerformance, ref adapterInterface, out adapterPtr);
                if (hr != 0 || adapterPtr == IntPtr.Zero)
                {
                    break;
                }

                var adapter = (IDXGIAdapter1)Marshal.GetObjectForIUnknown(adapterPtr);
                Marshal.Release(adapterPtr);
                try
                {
                    if (adapter.GetDesc1(out var desc) != 0 || (desc.Flags & SoftwareAdapter) != 0)
                    {
                        continue;
                    }

                    var drivesDisplay = adapter.EnumOutputs(0, out var outputPtr) == 0 && outputPtr != IntPtr.Zero;
                    if (outputPtr != IntPtr.Zero)
                    {
                        Marshal.Release(outputPtr);
                    }

                    candidates.Add(new Adapter(
                        string.IsNullOrWhiteSpace(desc.Description) ? null : desc.Description.Trim(),
                        desc.AdapterLuid, desc.VendorId, desc.DeviceId, (long)desc.DedicatedVideoMemory, drivesDisplay));
                }
                finally
                {
                    Marshal.ReleaseComObject(adapter);
                }
            }
        }
        finally
        {
            if (preferredFactory != IntPtr.Zero)
            {
                Marshal.Release(preferredFactory);
            }
            Marshal.Release(factoryPtr);
            Marshal.ReleaseComObject(factory);
        }

        return candidates;
    }

    [DllImport("dxgi.dll", PreserveSig = true)]
    private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr ppFactory);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumAdapterByGpuPreference(IntPtr factory, uint index, uint preference, ref Guid iid, out IntPtr adapter);

    // Placeholder members preserve the native vtable offsets.
    [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIFactory1
    {
        void _SetPrivateData();
        void _SetPrivateDataInterface();
        void _GetPrivateData();
        void _GetParent();
        void _EnumAdapters();
        void _MakeWindowAssociation();
        void _GetWindowAssociation();
        void _CreateSwapChain();
        void _CreateSoftwareAdapter();
        [PreserveSig] int EnumAdapters1(uint index, out IntPtr adapter);
        [PreserveSig] int IsCurrent();
    }

    [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter1
    {
        void _SetPrivateData();
        void _SetPrivateDataInterface();
        void _GetPrivateData();
        void _GetParent();
        [PreserveSig] int EnumOutputs(uint index, out IntPtr output);
        void _GetDesc();
        void _CheckInterfaceSupport();
        [PreserveSig] int GetDesc1(out DxgiAdapterDesc1 desc);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DxgiAdapterDesc1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public long AdapterLuid;
        public uint Flags;
    }
}
