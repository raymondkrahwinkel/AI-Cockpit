namespace Cockpit.Core.Voice;

// Resolves which NuGet package carries a backend's native libraries and the layout Whisper.net's loader expects.
// Sits next to `WhisperBackendPlanner`: the planner decides the try-order, this decides what must be on disk.
// AC-1013: GPU runtimes fetch on first use (unpickable at build time, and bundling all cost 1.5 GB); CPU stays bundled since it's small and always the fallback.
public static class WhisperRuntimeCatalog
{
    public static WhisperRuntimePackage? Resolve(WhisperRuntimeBackend backend, WhisperHostPlatform platform, string architecture)
    {
        var packageId = _ResolvePackageId(backend, platform);
        var runtimeFolder = _ResolveRuntimeFolder(backend);
        if (packageId is null || runtimeFolder is null)
        {
            return null;
        }

        var rid = $"{PathSegment(platform)}-{architecture}";

        return new WhisperRuntimePackage(packageId, $"build/{rid}", Path.Combine("runtimes", runtimeFolder, rid));
    }

    // What Whisper.net's own loader calls this platform when it builds a runtime path — its scheme, which is
    // not the NuGet RID's (`macos`, not `osx`).
    public static string PathSegment(WhisperHostPlatform platform) => platform switch
    {
        WhisperHostPlatform.Windows => "win",
        WhisperHostPlatform.Linux => "linux",
        WhisperHostPlatform.MacOs => "macos",
        _ => throw new ArgumentOutOfRangeException(nameof(platform), platform, "Unmapped Whisper host platform."),
    };

    // Turns the cached `runtimes/` directory into `RuntimeOptions.LibraryPath`: Whisper.net treats that
    // option as a file path and takes its directory, so the trailing separator is what makes it resolve
    // here instead of the parent — without it, lookup silently falls back to the CPU.
    public static string ToLibrarySearchPath(string runtimeRoot) =>
        runtimeRoot.EndsWith(Path.DirectorySeparatorChar) ? runtimeRoot : runtimeRoot + Path.DirectorySeparatorChar;

    // The runtime version to fetch, read from Whisper.net's own informational version — the natives have to
    // match the library loading them. Strips SemVer build metadata (`1.9.1+abc123` becomes `1.9.1`)
    // but keeps a prerelease suffix, which is the part the assembly version would silently drop.
    public static string NormalizePackageVersion(string informationalVersion)
    {
        var buildMetadata = informationalVersion.IndexOf('+');

        return buildMetadata < 0 ? informationalVersion : informationalVersion[..buildMetadata];
    }

    private static string? _ResolvePackageId(WhisperRuntimeBackend backend, WhisperHostPlatform platform) => backend switch
    {
        // The un-suffixed Whisper.net.Runtime.Cuda/Cuda12 are meta-packages carrying nothing but a readme and a
        // dependency on these two. Fetching those would cache an empty runtime, which the loader skips in
        // silence — the failure is a slow CPU transcription nobody can see the cause of.
        WhisperRuntimeBackend.Cuda => platform switch
        {
            WhisperHostPlatform.Windows => "Whisper.net.Runtime.Cuda.Windows",
            WhisperHostPlatform.Linux => "Whisper.net.Runtime.Cuda.Linux",
            _ => null,
        },
        WhisperRuntimeBackend.Cuda12 => platform switch
        {
            WhisperHostPlatform.Windows => "Whisper.net.Runtime.Cuda12.Windows",
            WhisperHostPlatform.Linux => "Whisper.net.Runtime.Cuda12.Linux",
            _ => null,
        },
        // Vulkan is not split per OS: one package holds both the win-x64 and the linux-x64 natives.
        WhisperRuntimeBackend.Vulkan =>
            platform is WhisperHostPlatform.Windows or WhisperHostPlatform.Linux ? "Whisper.net.Runtime.Vulkan" : null,
        // The CPU runtimes are bundled, and macOS has no GPU package at all — its Metal acceleration ships
        // inside that bundled CPU runtime rather than as a family of its own.
        _ => null,
    };

    private static string? _ResolveRuntimeFolder(WhisperRuntimeBackend backend) => backend switch
    {
        WhisperRuntimeBackend.Cuda => "cuda",
        WhisperRuntimeBackend.Cuda12 => "cuda12",
        WhisperRuntimeBackend.Vulkan => "vulkan",
        _ => null,
    };
}
