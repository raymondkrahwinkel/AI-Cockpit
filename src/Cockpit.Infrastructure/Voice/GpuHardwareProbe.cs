using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Cockpit.Core.Abstractions.Voice;

namespace Cockpit.Infrastructure.Voice;

// AC-68 slice 2: Identifies the host GPUs beyond "can a runtime load" — brand, whether one drives a monitor
// (transcribing on the display GPU stutters the desktop), and VRAM. AC-1881: Vulkan is the GPU list, enriched by
// DXGI (Windows, via LUID) or sysfs (Linux); every path degrades to fewer facts rather than throwing.
internal static class GpuHardwareProbe
{
    private const uint DxgiAdapterFlagSoftware = 2;
    private const int DxgiErrorNotFound = unchecked((int)0x887A0002);

    public static IReadOnlyList<GpuHardware> DetectAll()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return _Merge(VulkanDeviceProbe.Enumerate(), _DetectWindows(), vk => vk.Luid is { } luid ? gpu => gpu.Luid == luid : null);
            }

            if (OperatingSystem.IsLinux())
            {
                return _Merge(VulkanDeviceProbe.Enumerate(), _DetectLinux(), vk => gpu => gpu.VendorId == vk.VendorId && gpu.PciDeviceId == vk.PciDeviceId);
            }

            if (OperatingSystem.IsMacOS() && RuntimeInformation.OSArchitecture == Architecture.Arm64)
            {
                return [new GpuHardware(GpuVendor.Apple, "Apple Silicon", DrivesDisplay: true, VideoMemoryBytes: 0)];
            }
        }
        catch
        {
            // A probe is a nice-to-have; never let a driver quirk or a missing library take down the dialog.
        }

        return [];
    }

    // AC-1881: One entry per Vulkan device (deduplicated on UUID: DXGI can list one iGPU twice), with "drives display"
    // and VRAM from the OS probe that matches it. Without Vulkan the OS probe's own list stands, unselectable.
    // ponytail: Linux matches on PCI vendor/device id, so two identical cards share one display flag; add pci_bus_info if that bites.
    private static IReadOnlyList<GpuHardware> _Merge(
        IReadOnlyList<VulkanDevice> vulkan,
        IReadOnlyList<OsAdapter> os,
        Func<VulkanDevice, Func<OsAdapter, bool>?> matcher)
    {
        if (vulkan.Count == 0)
        {
            return os.Select(adapter => adapter.Gpu).ToList();
        }

        var gpus = new List<GpuHardware>();
        foreach (var device in vulkan.DistinctBy(device => device.Uuid))
        {
            var match = matcher(device) is { } predicate ? os.FirstOrDefault(predicate) : null;
            var vram = match?.Gpu.VideoMemoryBytes is > 0 and var osVram ? osVram : device.Kind is GpuKind.Discrete ? device.LocalHeapBytes : 0;
            gpus.Add(new GpuHardware(_Vendor(device.VendorId), match?.Gpu.Description ?? device.Name, match?.Gpu.DrivesDisplay ?? false, vram)
            {
                Kind = device.Kind,
                DeviceId = device.Uuid,
                VulkanIndex = device.Index,
            });
        }

        return gpus;
    }

    // One adapter as the OS reports it, with the keys used to match it to a Vulkan device.
    private sealed record OsAdapter(GpuHardware Gpu, long? Luid, uint VendorId, uint PciDeviceId);

    private static GpuVendor _Vendor(uint vendorId) => vendorId switch
    {
        0x10DE => GpuVendor.Nvidia,
        0x1002 or 0x1022 => GpuVendor.Amd,
        0x8086 or 0x8087 => GpuVendor.Intel,
        0x106B => GpuVendor.Apple,
        _ => GpuVendor.Unknown,
    };

    // ── Windows: DXGI ────────────────────────────────────────────────────────────────────────────────────────

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<OsAdapter> _DetectWindows()
    {
        var iid = typeof(IDXGIFactory1).GUID;
        if (CreateDXGIFactory1(ref iid, out var factoryPtr) != 0 || factoryPtr == IntPtr.Zero)
        {
            return [];
        }

        var factory = (IDXGIFactory1)Marshal.GetObjectForIUnknown(factoryPtr);
        Marshal.Release(factoryPtr);

        var candidates = new List<OsAdapter>();
        try
        {
            for (uint index = 0; ; index++)
            {
                var hr = factory.EnumAdapters1(index, out var adapterPtr);
                if (hr == DxgiErrorNotFound || hr != 0 || adapterPtr == IntPtr.Zero)
                {
                    break;
                }

                var adapter = (IDXGIAdapter1)Marshal.GetObjectForIUnknown(adapterPtr);
                Marshal.Release(adapterPtr);
                try
                {
                    if (adapter.GetDesc1(out var desc) != 0 || (desc.Flags & DxgiAdapterFlagSoftware) != 0)
                    {
                        continue;
                    }

                    // An adapter drives the display when it exposes at least one output.
                    var drivesDisplay = adapter.EnumOutputs(0, out var outputPtr) == 0 && outputPtr != IntPtr.Zero;
                    if (outputPtr != IntPtr.Zero)
                    {
                        Marshal.Release(outputPtr);
                    }

                    candidates.Add(new OsAdapter(
                        new GpuHardware(
                            _Vendor(desc.VendorId),
                            string.IsNullOrWhiteSpace(desc.Description) ? null : desc.Description.Trim(),
                            drivesDisplay,
                            (long)(ulong)desc.DedicatedVideoMemory),
                        desc.AdapterLuid,
                        desc.VendorId,
                        desc.DeviceId));
                }
                finally
                {
                    Marshal.ReleaseComObject(adapter);
                }
            }
        }
        finally
        {
            Marshal.ReleaseComObject(factory);
        }

        return candidates;
    }

    [DllImport("dxgi.dll", PreserveSig = true)]
    private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr ppFactory);

    // Only the methods actually called are given real signatures; the earlier vtable slots are placeholders so
    // the COM interop lands EnumAdapters1 / EnumOutputs / GetDesc1 on the right offsets.
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

    // ── Linux: sysfs (best-effort) ───────────────────────────────────────────────────────────────────────────

    private static IReadOnlyList<OsAdapter> _DetectLinux()
    {
        const string drm = "/sys/class/drm";
        if (!Directory.Exists(drm))
        {
            return [];
        }

        var adapters = new List<OsAdapter>();
        foreach (var card in Directory.EnumerateDirectories(drm, "card?"))
        {
            var vendorFile = Path.Combine(card, "device", "vendor");
            if (!File.Exists(vendorFile))
            {
                continue;
            }

            var vendorId = _ParseHex(File.ReadAllText(vendorFile));
            var vendor = _Vendor(vendorId);
            if (vendor is GpuVendor.Unknown)
            {
                continue;
            }

            var deviceFile = Path.Combine(card, "device", "device");
            var deviceId = File.Exists(deviceFile) ? _ParseHex(File.ReadAllText(deviceFile)) : 0;

            // A connected connector under this card means it is driving a display.
            var cardName = Path.GetFileName(card);
            var drivesDisplay = Directory.EnumerateDirectories(drm, cardName + "-*")
                .Select(connector => Path.Combine(connector, "status"))
                .Any(status => File.Exists(status) && File.ReadAllText(status).Trim() == "connected");

            adapters.Add(new OsAdapter(new GpuHardware(vendor, Description: null, drivesDisplay, VideoMemoryBytes: 0), Luid: null, vendorId, deviceId));
        }

        return adapters;
    }

    private static uint _ParseHex(string raw)
    {
        var text = raw.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            text = text[2..];
        }

        return uint.TryParse(text, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;
    }
}
