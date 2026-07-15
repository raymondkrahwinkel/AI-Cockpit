namespace Cockpit.Core.Voice;

/// <summary>
/// Resolves which NuGet package carries a backend's native libraries and the layout Whisper.net's loader
/// expects to find them in. Sits next to <see cref="WhisperBackendPlanner"/> on purpose: the planner decides
/// the try-order, this decides what has to be on disk for an entry in that order to be tryable at all.
/// <para>
/// The GPU runtimes are fetched on first use instead of shipped — they cannot be picked at build time, since
/// which GPU a machine has is not knowable then, and bundling all of them cost a win-x64 publish 1.5 GB (the
/// natives weigh ~748 MB and a single-file publish carried them twice). The CPU runtimes are not here because
/// they stay bundled: small, work everywhere, and the floor transcription always falls back to.
/// </para>
/// </summary>
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

    /// <summary>
    /// What Whisper.net's own loader calls this platform when it builds a runtime path — its scheme, which is
    /// not the NuGet RID's (<c>macos</c>, not <c>osx</c>).
    /// </summary>
    public static string PathSegment(WhisperHostPlatform platform) => platform switch
    {
        WhisperHostPlatform.Windows => "win",
        WhisperHostPlatform.Linux => "linux",
        WhisperHostPlatform.MacOs => "macos",
        _ => throw new ArgumentOutOfRangeException(nameof(platform), platform, "Unmapped Whisper host platform."),
    };

    /// <summary>
    /// Turns the directory holding the cached <c>runtimes/</c> tree into the value Whisper.net wants as
    /// <c>RuntimeOptions.LibraryPath</c>. It reads that option as a path to a <em>file</em> and takes its
    /// directory, so the trailing separator is what makes it resolve to this folder instead of its parent —
    /// and a parent lookup finds no runtime at all, silently, on the CPU.
    /// </summary>
    public static string ToLibrarySearchPath(string runtimeRoot) =>
        runtimeRoot.EndsWith(Path.DirectorySeparatorChar) ? runtimeRoot : runtimeRoot + Path.DirectorySeparatorChar;

    /// <summary>
    /// The runtime version to fetch, read from Whisper.net's own informational version — the natives have to
    /// match the library loading them. Strips SemVer build metadata (<c>1.9.1+abc123</c> becomes <c>1.9.1</c>)
    /// but keeps a prerelease suffix, which is the part the assembly version would silently drop.
    /// </summary>
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
