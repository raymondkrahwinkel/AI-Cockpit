using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Cockpit.Core.Abstractions.Voice;

namespace Cockpit.Infrastructure.Voice;

/// <summary>
/// Identifies the display GPU beyond "can a runtime load" (AC-68 slice 2): brand, description, whether it also
/// drives a monitor, and its dedicated VRAM. The whole point is the "drives a monitor" fact — a single GPU that
/// renders the desktop should not also be transcribing, or the desktop stutters — which no existing probe knew.
/// <para>
/// Windows is first-class via DXGI (accurate VRAM and per-adapter outputs). Linux is best-effort from sysfs
/// (vendor + whether a connector is connected; no VRAM). macOS is reported as Apple on Apple Silicon — the
/// recommender routes macOS to Metal by platform regardless. Every path is wrapped so a probe failure degrades
/// to <see cref="GpuHardware.None"/> (treated as CPU-only) rather than throwing into the Options dialog.
/// </para>
/// </summary>
internal static class GpuHardwareProbe
{
    private const uint DxgiAdapterFlagSoftware = 2;
    private const int DxgiErrorNotFound = unchecked((int)0x887A0002);

    public static GpuHardware Detect()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return _DetectWindows();
            }

            if (OperatingSystem.IsLinux())
            {
                return _DetectLinux();
            }

            if (OperatingSystem.IsMacOS())
            {
                return RuntimeInformation.OSArchitecture == Architecture.Arm64
                    ? new GpuHardware(GpuVendor.Apple, "Apple Silicon", DrivesDisplay: true, VideoMemoryBytes: 0)
                    : GpuHardware.None;
            }
        }
        catch
        {
            // A probe is a nice-to-have; never let a driver quirk or a missing library take down the dialog.
        }

        return GpuHardware.None;
    }

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
    private static GpuHardware _DetectWindows()
    {
        var iid = typeof(IDXGIFactory1).GUID;
        if (CreateDXGIFactory1(ref iid, out var factoryPtr) != 0 || factoryPtr == IntPtr.Zero)
        {
            return GpuHardware.None;
        }

        var factory = (IDXGIFactory1)Marshal.GetObjectForIUnknown(factoryPtr);
        Marshal.Release(factoryPtr);

        var candidates = new List<GpuHardware>();
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

                    candidates.Add(new GpuHardware(
                        _Vendor(desc.VendorId),
                        string.IsNullOrWhiteSpace(desc.Description) ? null : desc.Description.Trim(),
                        drivesDisplay,
                        (long)(ulong)desc.DedicatedVideoMemory));
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

        // Prefer the adapter that actually drives a monitor (that is the one transcription would fight); among
        // ties, the one with the most VRAM. If nothing drives a display, fall back to the largest card.
        return candidates
            .OrderByDescending(gpu => gpu.DrivesDisplay)
            .ThenByDescending(gpu => gpu.VideoMemoryBytes)
            .FirstOrDefault() ?? GpuHardware.None;
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

    private static GpuHardware _DetectLinux()
    {
        const string drm = "/sys/class/drm";
        if (!Directory.Exists(drm))
        {
            return GpuHardware.None;
        }

        foreach (var card in Directory.EnumerateDirectories(drm, "card?"))
        {
            var vendorFile = Path.Combine(card, "device", "vendor");
            if (!File.Exists(vendorFile))
            {
                continue;
            }

            var vendor = _Vendor(_ParseHex(File.ReadAllText(vendorFile)));
            if (vendor is GpuVendor.Unknown)
            {
                continue;
            }

            // A connected connector under this card means it is driving a display.
            var cardName = Path.GetFileName(card);
            var drivesDisplay = Directory.EnumerateDirectories(drm, cardName + "-*")
                .Select(connector => Path.Combine(connector, "status"))
                .Any(status => File.Exists(status) && File.ReadAllText(status).Trim() == "connected");

            return new GpuHardware(vendor, Description: null, drivesDisplay, VideoMemoryBytes: 0);
        }

        return GpuHardware.None;
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
