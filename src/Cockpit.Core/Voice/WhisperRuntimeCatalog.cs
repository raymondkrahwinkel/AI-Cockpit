namespace Cockpit.Core.Voice;

/// <summary>
/// Resolves which NuGet package carries a backend's native libraries and the layout Whisper.net's loader
/// expects to find them in. Sits next to <see cref="WhisperBackendPlanner"/> on purpose: the planner decides
/// the try-order, this decides what has to be on disk for an entry in that order to be tryable at all.
/// <para>
/// The GPU runtimes are ~1.2 GB and are fetched on first use instead of shipped (they cannot be picked at
/// build time — which GPU a machine has is not knowable then). The CPU runtimes are not here because they
/// stay bundled with the app: small, work everywhere, and the floor transcription always falls back to.
/// </para>
/// </summary>
public static class WhisperRuntimeCatalog
{
    public static WhisperRuntimePackage? Resolve(WhisperRuntimeBackend backend, string platform, string architecture)
    {
        var packageId = _ResolvePackageId(backend, platform);
        var runtimeFolder = _ResolveRuntimeFolder(backend);
        if (packageId is null || runtimeFolder is null)
        {
            return null;
        }

        return new WhisperRuntimePackage(
            packageId,
            $"build/{platform}-{architecture}",
            Path.Combine("runtimes", runtimeFolder, $"{platform}-{architecture}"));
    }

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

    private static string? _ResolvePackageId(WhisperRuntimeBackend backend, string platform) => backend switch
    {
        // The un-suffixed Whisper.net.Runtime.Cuda/Cuda12 are meta-packages carrying nothing but a readme and a
        // dependency on these two. Fetching those would cache an empty runtime, which the loader skips in
        // silence — the failure is a slow CPU transcription nobody can see the cause of.
        WhisperRuntimeBackend.Cuda => platform switch
        {
            "win" => "Whisper.net.Runtime.Cuda.Windows",
            "linux" => "Whisper.net.Runtime.Cuda.Linux",
            _ => null,
        },
        WhisperRuntimeBackend.Cuda12 => platform switch
        {
            "win" => "Whisper.net.Runtime.Cuda12.Windows",
            "linux" => "Whisper.net.Runtime.Cuda12.Linux",
            _ => null,
        },
        // Vulkan is not split per OS: one package holds both the win-x64 and linux-x64 natives.
        WhisperRuntimeBackend.Vulkan => platform is "win" or "linux" ? "Whisper.net.Runtime.Vulkan" : null,
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
