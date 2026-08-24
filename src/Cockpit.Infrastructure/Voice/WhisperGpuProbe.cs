using System.Runtime.InteropServices;
using Cockpit.Core.Voice;

namespace Cockpit.Infrastructure.Voice;

// AC-1013: Answers whether this machine can actually use a GPU backend before `WhisperRuntimeCache` spends
// hundreds of megabytes fetching a runtime. The CUDA probe deliberately mirrors Whisper.net's own `CudaHelper`
// (tag 1.9.1: Cuda↔major 13, Cuda12↔major 12, mismatch rejected) so we never fetch/skip against its own choice.
internal static class WhisperGpuProbe
{
    private const int CudaSuccess = 0;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CudaGetDeviceCount(out int count);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CudaRuntimeGetVersion(out int version);

    public static bool IsUsable(WhisperRuntimeBackend backend) => backend switch
    {
        WhisperRuntimeBackend.Cuda => _HasCudaDevice(expectedMajorVersion: 13),
        WhisperRuntimeBackend.Cuda12 => _HasCudaDevice(expectedMajorVersion: 12),
        WhisperRuntimeBackend.Vulkan => _CanLoadAny(_VulkanLibraryNames()),
        // The CPU runtimes are bundled, so there is nothing to fetch and nothing to probe.
        _ => false,
    };

    // A loadable cudart of the expected major version, reporting at least one device. The version check is the
    // point: a host with CUDA 12 loads a cudart just fine, and only the major tells us the CUDA-13 natives
    // would be refused.
    private static bool _HasCudaDevice(int expectedMajorVersion)
    {
        foreach (var libraryName in _CudartLibraryNames(expectedMajorVersion))
        {
            if (!NativeLibrary.TryLoad(libraryName, out var library))
            {
                continue;
            }

            try
            {
                if (_CudaMajorVersion(library) == expectedMajorVersion && _CudaDeviceCount(library) > 0)
                {
                    return true;
                }
            }
            finally
            {
                NativeLibrary.Free(library);
            }
        }

        return false;
    }

    private static int? _CudaMajorVersion(nint library)
    {
        if (!NativeLibrary.TryGetExport(library, "cudaRuntimeGetVersion", out var export))
        {
            return null;
        }

        var cudaRuntimeGetVersion = Marshal.GetDelegateForFunctionPointer<CudaRuntimeGetVersion>(export);

        return cudaRuntimeGetVersion(out var version) == CudaSuccess ? version / 1000 : null;
    }

    private static int _CudaDeviceCount(nint library)
    {
        if (!NativeLibrary.TryGetExport(library, "cudaGetDeviceCount", out var export))
        {
            return 0;
        }

        var cudaGetDeviceCount = Marshal.GetDelegateForFunctionPointer<CudaGetDeviceCount>(export);

        return cudaGetDeviceCount(out var count) == CudaSuccess ? count : 0;
    }

    private static IEnumerable<string> _CudartLibraryNames(int majorVersion) =>
        OperatingSystem.IsWindows()
            ? [$"cudart64_{majorVersion}"]
            // The unversioned name is the fallback a distro-packaged CUDA often installs; the major check above
            // is what decides whether whatever it resolves to is the one we want.
            : [$"libcudart.so.{majorVersion}", "libcudart.so"];

    // AC-1013: Whether a Vulkan loader is installed — our own deliberately low bar since Whisper.net probes
    // nothing for Vulkan. Keeps a driverless machine from fetching 151 MB; a loader without a usable device
    // still ends up on the CPU floor (a proper VkInstance check was rejected as too much interop for a rare case).
    private static IEnumerable<string> _VulkanLibraryNames() =>
        OperatingSystem.IsWindows() ? ["vulkan-1"] : ["libvulkan.so.1", "libvulkan.so"];

    private static bool _CanLoadAny(IEnumerable<string> libraryNames)
    {
        foreach (var libraryName in libraryNames)
        {
            if (NativeLibrary.TryLoad(libraryName, out var library))
            {
                NativeLibrary.Free(library);

                return true;
            }
        }

        return false;
    }
}
