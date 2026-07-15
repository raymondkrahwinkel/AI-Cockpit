using System.Runtime.InteropServices;
using Cockpit.Core.Voice;

namespace Cockpit.Infrastructure.Voice;

/// <summary>
/// Answers whether this machine can actually use a GPU backend, before <see cref="WhisperRuntimeCache"/>
/// spends hundreds of megabytes fetching its runtime.
/// <para>
/// The CUDA probe mirrors Whisper.net's own <c>CudaHelper</c> (read at tag 1.9.1) deliberately: it decides
/// which runtimes it is willing to load, so any disagreement here means we either fetch a runtime it will
/// refuse or skip one it would have used. It pairs <see cref="WhisperRuntimeBackend.Cuda"/> with CUDA major
/// 13 and <see cref="WhisperRuntimeBackend.Cuda12"/> with major 12, and rejects a mismatch — which is why
/// both exist rather than one: CUDA-13 natives on a CUDA-12.8 host fall silently back to CPU.
/// </para>
/// <para>
/// Probing before the download is not circular: cudart is not in the runtime packages (they hold only the
/// ggml/whisper natives), it comes from a system CUDA install.
/// </para>
/// </summary>
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

    /// <summary>
    /// A loadable cudart of the expected major version, reporting at least one device. The version check is the
    /// point: a host with CUDA 12 loads a cudart just fine, and only the major tells us the CUDA-13 natives
    /// would be refused.
    /// </summary>
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

    /// <summary>
    /// Whether a Vulkan loader is installed. Whisper.net probes nothing for Vulkan — it just tries the natives —
    /// so this is our own bar, and a deliberately low one: it keeps a machine with no GPU drivers at all from
    /// fetching 151 MB, but a loader present without a usable device still ends up on the CPU floor. Answering
    /// that properly needs a VkInstance, which is a lot of interop to save one download on a rare machine.
    /// </summary>
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
