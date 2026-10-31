using System.Runtime.InteropServices;
using Cockpit.Core.Abstractions.Voice;

namespace Cockpit.Infrastructure.Voice;

// One physical device as Vulkan enumerates it (AC-1881). `Index` is the position GGML_VK_VISIBLE_DEVICES selects by.
// `Uuid` is null when the driver reports none (all zeroes), which leaves the GPU listed but not selectable.
internal sealed record VulkanDevice(int Index, string Name, GpuKind Kind, uint VendorId, uint PciDeviceId, string? Uuid, long? Luid, long LocalHeapBytes);

// AC-1881: Lists the Vulkan devices whisper.cpp's Vulkan backend chooses from, in the same enumeration order, so a
// chosen GPU can be handed to the child as an index. Struct fields are read at fixed offsets of the 64-bit Vulkan
// ABI rather than mirroring VkPhysicalDeviceLimits; CPU (software) devices are skipped. Any failure yields none.
internal static class VulkanDeviceProbe
{
    private const int StructureTypeApplicationInfo = 0;
    private const int StructureTypeInstanceCreateInfo = 1;
    private const int StructureTypePhysicalDeviceProperties2 = 1000059001;
    private const int StructureTypePhysicalDeviceIdProperties = 1000071004;
    private const uint ApiVersion11 = (1u << 22) | (1u << 12);
    private const int DeviceTypeIntegrated = 1;
    private const int DeviceTypeDiscrete = 2;
    private const int DeviceTypeCpu = 4;
    private const uint MemoryHeapDeviceLocal = 1;
    private const int Incomplete = 5;

    // Offsets into VkPhysicalDeviceProperties2 / VkPhysicalDeviceIDProperties / VkPhysicalDeviceMemoryProperties.
    private const int PropsVendorId = 24;
    private const int PropsDeviceId = 28;
    private const int PropsDeviceType = 32;
    private const int PropsDeviceName = 36;
    private const int IdUuid = 16;
    private const int IdLuid = 48;
    private const int IdLuidValid = 60;
    private const int MemHeapCount = 260;
    private const int MemHeaps = 264;

    public static IReadOnlyList<VulkanDevice> Enumerate()
    {
        if (!Environment.Is64BitProcess || !_TryLoad(out var library))
        {
            return [];
        }

        // The loader stays loaded: some ICDs keep threads or exit hooks that crash once it is unloaded.
        try
        {
            return _Enumerate(library);
        }
        catch
        {
            // A broken driver or loader must not cost more than the device list.
            return [];
        }
    }

    private static bool _TryLoad(out IntPtr library)
    {
        string[] names = OperatingSystem.IsWindows() ? ["vulkan-1"] : ["libvulkan.so.1", "libvulkan.so"];
        foreach (var name in names)
        {
            if (NativeLibrary.TryLoad(name, out library))
            {
                return true;
            }
        }

        library = IntPtr.Zero;
        return false;
    }

    private static List<VulkanDevice> _Enumerate(IntPtr library)
    {
        var create = _Export<VkCreateInstance>(library, "vkCreateInstance");
        var destroy = _Export<VkDestroyInstance>(library, "vkDestroyInstance");
        var enumerate = _Export<VkEnumeratePhysicalDevices>(library, "vkEnumeratePhysicalDevices");
        var properties = _Export<VkGetPhysicalDeviceInfo>(library, "vkGetPhysicalDeviceProperties2");
        var memory = _Export<VkGetPhysicalDeviceInfo>(library, "vkGetPhysicalDeviceMemoryProperties");

        var appInfo = new VkApplicationInfo { SType = StructureTypeApplicationInfo, ApiVersion = ApiVersion11 };
        var appInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<VkApplicationInfo>());
        try
        {
            Marshal.StructureToPtr(appInfo, appInfoPtr, fDeleteOld: false);
            var createInfo = new VkInstanceCreateInfo { SType = StructureTypeInstanceCreateInfo, ApplicationInfo = appInfoPtr };
            if (create(ref createInfo, IntPtr.Zero, out var instance) != 0 || instance == IntPtr.Zero)
            {
                return [];
            }

            try
            {
                return _ReadDevices(instance, enumerate, properties, memory);
            }
            finally
            {
                destroy(instance, IntPtr.Zero);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(appInfoPtr);
        }
    }

    private static List<VulkanDevice> _ReadDevices(
        IntPtr instance, VkEnumeratePhysicalDevices enumerate, VkGetPhysicalDeviceInfo properties, VkGetPhysicalDeviceInfo memory)
    {
        uint count = 0;
        if (enumerate(instance, ref count, null) != 0 || count == 0)
        {
            return [];
        }

        var handles = new IntPtr[count];
        // VK_INCOMPLETE: a device appeared between the two calls; the ones returned are still valid.
        if (enumerate(instance, ref count, handles) is not (0 or Incomplete))
        {
            return [];
        }

        var devices = new List<VulkanDevice>();
        var props = Marshal.AllocHGlobal(1024);
        var ids = Marshal.AllocHGlobal(128);
        var mem = Marshal.AllocHGlobal(1024);
        try
        {
            for (var index = 0; index < count; index++)
            {
                _Zero(props, 1024);
                _Zero(ids, 128);
                _Zero(mem, 1024);
                Marshal.WriteInt32(props, 0, StructureTypePhysicalDeviceProperties2);
                Marshal.WriteIntPtr(props, 8, ids);
                Marshal.WriteInt32(ids, 0, StructureTypePhysicalDeviceIdProperties);
                properties(handles[index], props);
                memory(handles[index], mem);

                var type = Marshal.ReadInt32(props, PropsDeviceType);
                if (type == DeviceTypeCpu)
                {
                    continue;
                }

                var uuid = new byte[16];
                Marshal.Copy(ids + IdUuid, uuid, 0, uuid.Length);
                long? luid = Marshal.ReadInt32(ids, IdLuidValid) != 0 ? Marshal.ReadInt64(ids, IdLuid) : null;
                var hasUuid = uuid.Any(value => value != 0);

                devices.Add(new VulkanDevice(
                    index,
                    Marshal.PtrToStringUTF8(props + PropsDeviceName) ?? string.Empty,
                    type switch { DeviceTypeDiscrete => GpuKind.Discrete, DeviceTypeIntegrated => GpuKind.Integrated, _ => GpuKind.Unknown },
                    (uint)Marshal.ReadInt32(props, PropsVendorId),
                    (uint)Marshal.ReadInt32(props, PropsDeviceId),
                    hasUuid ? new Guid(uuid, bigEndian: true).ToString() : null,
                    luid,
                    _LargestDeviceLocalHeap(mem)));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(props);
            Marshal.FreeHGlobal(ids);
            Marshal.FreeHGlobal(mem);
        }

        return devices;
    }

    private static long _LargestDeviceLocalHeap(IntPtr mem)
    {
        var largest = 0L;
        var heapCount = Math.Min(Marshal.ReadInt32(mem, MemHeapCount), 16);
        for (var heap = 0; heap < heapCount; heap++)
        {
            var size = Marshal.ReadInt64(mem, MemHeaps + (heap * 16));
            var flags = (uint)Marshal.ReadInt32(mem, MemHeaps + (heap * 16) + 8);
            if ((flags & MemoryHeapDeviceLocal) != 0 && size > largest)
            {
                largest = size;
            }
        }

        return largest;
    }

    private static void _Zero(IntPtr buffer, int length)
    {
        for (var offset = 0; offset < length; offset += 8)
        {
            Marshal.WriteInt64(buffer, offset, 0);
        }
    }

    private static T _Export<T>(IntPtr library, string name)
        where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int VkCreateInstance(ref VkInstanceCreateInfo createInfo, IntPtr allocator, out IntPtr instance);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void VkDestroyInstance(IntPtr instance, IntPtr allocator);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int VkEnumeratePhysicalDevices(IntPtr instance, ref uint count, [Out] IntPtr[]? devices);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void VkGetPhysicalDeviceInfo(IntPtr physicalDevice, IntPtr output);

    [StructLayout(LayoutKind.Sequential)]
    private struct VkApplicationInfo
    {
        public int SType;
        public IntPtr Next;
        public IntPtr ApplicationName;
        public uint ApplicationVersion;
        public IntPtr EngineName;
        public uint EngineVersion;
        public uint ApiVersion;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VkInstanceCreateInfo
    {
        public int SType;
        public IntPtr Next;
        public uint Flags;
        public IntPtr ApplicationInfo;
        public uint EnabledLayerCount;
        public IntPtr EnabledLayerNames;
        public uint EnabledExtensionCount;
        public IntPtr EnabledExtensionNames;
    }
}
