using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using Cockpit.Core.Voice;
using Cockpit.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Whisper.net;

namespace Cockpit.Infrastructure.Voice;

/// <summary>
/// Fetches the GPU runtime this machine can actually use on first dictation and caches it next to the model
/// cache — the same lazy shape as <see cref="WhisperModelCache"/>, and for the same reason: which GPU a
/// machine has is not knowable at build time, so bundling every runtime ships ~1.2 GB of CUDA and Vulkan
/// natives to every install to serve whichever one is right for that one machine.
/// <para>
/// Only the first backend in the planner's order that <see cref="WhisperGpuProbe"/> calls usable is fetched —
/// not all three. Nothing usable, or the fetch fails, and the bundled CPU runtime carries transcription. A
/// missing GPU runtime is only ever slower, never fatal: nothing on this path may take dictation down with it.
/// </para>
/// </summary>
internal static class WhisperRuntimeCache
{
    /// <summary>
    /// One client for the process, like <c>WhisperGgmlDownloader.Default</c> keeps: a client per download
    /// churns sockets. <see cref="HttpCompletionOption.ResponseHeadersRead"/> keeps this timeout on the
    /// handshake instead of the body — a runtime is hundreds of megabytes and a slow line is not a failure.
    /// </summary>
    private static readonly HttpClient NuGetClient = new() { Timeout = TimeSpan.FromMinutes(2) };

    /// <summary>
    /// The runtime version to fetch, taken from the Whisper.net assembly that will load it, so the two can
    /// never drift: a bump of the library moves this with it. Asking NuGet for the newest instead would
    /// reintroduce the mismatch this whole split exists to avoid — and a mismatch fails silently on the CPU.
    /// </summary>
    private static readonly string RuntimeVersion = WhisperRuntimeCatalog.NormalizePackageVersion(
        typeof(WhisperFactory).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(WhisperFactory).Assembly.GetName().Version?.ToString(3)
        ?? throw new InvalidOperationException("The Whisper.net assembly carries no version to match its native runtimes against."));

    /// <summary>What to hand Whisper.net as <c>RuntimeOptions.LibraryPath</c>.</summary>
    public static string SearchPath => WhisperRuntimeCatalog.ToLibrarySearchPath(VersionRoot);

    private static string RuntimesRoot => Path.Combine(CockpitConfigPath.Root, "whisper-runtimes");

    /// <summary>
    /// Runtimes live under the Whisper.net version they were published for. Keeping the version in the path is
    /// what makes a library bump safe without any migration: the old natives are not stale, they are simply not
    /// where the new loader looks. <see cref="_RemoveOtherVersions"/> reclaims them afterwards.
    /// </summary>
    private static string VersionRoot => Path.Combine(RuntimesRoot, RuntimeVersion);

    private static string WhisperLibraryFileName => OperatingSystem.IsWindows() ? "whisper.dll" : "libwhisper.so";

    /// <summary>
    /// Makes sure the best runtime this machine can use is on disk before the factory is built. Walks the
    /// planner's order and stops at the first backend that is both usable here and cached (or fetchable).
    /// </summary>
    public static async Task EnsureAvailableAsync(
        IReadOnlyList<WhisperRuntimeBackend> order,
        WhisperHostPlatform platform,
        CancellationToken cancellationToken,
        ILogger? logger = null)
    {
        var architecture = _CurrentArchitecture();
        if (architecture is null)
        {
            return;
        }

        // Before the walk, not after a successful one: a machine that has stopped being able to use any GPU at
        // all still has hundreds of megabytes of superseded natives to give back.
        RemoveOtherVersions(RuntimesRoot, RuntimeVersion, logger);

        foreach (var backend in order)
        {
            try
            {
                var package = WhisperRuntimeCatalog.Resolve(backend, platform, architecture);

                // The CPU tail resolves to nothing (it is bundled) and so does a backend with no package for this
                // platform. Probing before the cache check matters: a machine that traded its NVIDIA card for an
                // AMD one still has the CUDA runtime cached, and stopping there would leave Vulkan unfetched.
                if (package is null || !WhisperGpuProbe.IsUsable(backend))
                {
                    continue;
                }

                if (_IsCached(package) || await _TryFetchAsync(package, backend, cancellationToken, logger).ConfigureAwait(false))
                {
                    return;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // The probe calls into a native cudart this machine owns and we do not. A broken one is its
                // problem to have, not dictation's to die of — say so and let the next backend, or the CPU, run.
                logger?.LogWarning(
                    exception, "Whisper {Backend} runtime could not be provisioned; trying the next backend", backend);
            }
        }
    }

    private static bool _IsCached(WhisperRuntimePackage package) =>
        File.Exists(Path.Combine(VersionRoot, package.CacheSubPath, WhisperLibraryFileName));

    private static async Task<bool> _TryFetchAsync(
        WhisperRuntimePackage package, WhisperRuntimeBackend backend, CancellationToken cancellationToken, ILogger? logger)
    {
        var destination = Path.Combine(VersionRoot, package.CacheSubPath);

        // Scratch paths carry the process id so two cockpits warming a cold cache cannot trample each other's
        // half-written files. They still race for the destination, and the loser degrades to CPU with a warning.
        var staging = $"{destination}.{Environment.ProcessId}.fetching";
        var packageFile = Path.Combine(VersionRoot, $"{package.PackageId}.{RuntimeVersion}.{Environment.ProcessId}.nupkg.download");

        try
        {
            // First use on this machine. Logged loudly for the same reason the model download is: the operator
            // sees a slow transcription, and without this line there is nothing anywhere saying why.
            logger?.LogInformation(
                "Whisper {Backend} runtime is not cached yet; fetching {Package} {Version} from NuGet now (first use on this machine — a few hundred MB, transcription runs on the CPU until it lands)",
                backend, package.PackageId, RuntimeVersion);
            var stopwatch = Stopwatch.StartNew();

            Directory.CreateDirectory(VersionRoot);
            await _DownloadPackageAsync(package, packageFile, cancellationToken).ConfigureAwait(false);

            // Extract beside the destination and swap it in only once it is whole, so an interrupted fetch can
            // never leave a half-filled directory that the next run reads as a complete, cached runtime.
            _DeleteDirectory(staging);
            Directory.CreateDirectory(staging);
            var extracted = ExtractNatives(packageFile, package, staging);
            if (extracted == 0)
            {
                logger?.LogWarning(
                    "Whisper {Backend} runtime package {Package} {Version} carried no natives under {Folder}; staying on the CPU runtime",
                    backend, package.PackageId, RuntimeVersion, package.PackageNativeFolder);

                return false;
            }

            _DeleteDirectory(destination);
            Directory.Move(staging, destination);

            logger?.LogInformation(
                "Whisper {Backend} runtime cached at {Path} ({Files} native files, {Seconds:F0}s)",
                backend, destination, extracted, stopwatch.Elapsed.TotalSeconds);

            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A GPU runtime that will not download is a slower cockpit, not a broken one — the CPU runtime is
            // bundled and the loader falls through to it. Warn and carry on rather than take dictation down.
            logger?.LogWarning(
                exception,
                "Whisper {Backend} runtime could not be fetched ({Package} {Version}); transcription falls back to the CPU runtime",
                backend, package.PackageId, RuntimeVersion);

            return false;
        }
        finally
        {
            _DeleteQuietly(staging, logger);
            _DeleteQuietly(packageFile, logger);
        }
    }

    /// <summary>
    /// A .nupkg is a zip, and <see cref="ZipArchive"/> needs to seek, which an HTTP response stream cannot —
    /// so it goes to a file first rather than into several hundred megabytes of memory.
    /// </summary>
    private static async Task _DownloadPackageAsync(
        WhisperRuntimePackage package, string packageFile, CancellationToken cancellationToken)
    {
        var id = package.PackageId.ToLowerInvariant();
        var version = RuntimeVersion.ToLowerInvariant();
        var url = $"https://api.nuget.org/v3-flatcontainer/{id}/{version}/{id}.{version}.nupkg";

        using var response = await NuGetClient
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var target = File.Create(packageFile);
        await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Flattens the package's native folder into <paramref name="staging"/>. The whole runtime comes over, not
    /// just whisper itself: the loader opens a dependency chain (ggml-base, ggml-cpu, ggml-cuda, ggml, …) out of
    /// the same directory, and a missing link there means it quietly moves on to the next backend.
    /// </summary>
    internal static int ExtractNatives(string packageFile, WhisperRuntimePackage package, string staging)
    {
        using var archive = ZipFile.OpenRead(packageFile);
        var nativeFolder = package.PackageNativeFolder + "/";
        var extracted = 0;

        foreach (var entry in archive.Entries)
        {
            // Only the file name is ever joined onto the staging path, so a crafted entry cannot escape it.
            if (entry.Name.Length == 0 || !entry.FullName.StartsWith(nativeFolder, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            entry.ExtractToFile(Path.Combine(staging, entry.Name), overwrite: true);
            extracted++;
        }

        return extracted;
    }

    /// <summary>
    /// Drops runtimes cached for a Whisper.net version we no longer load. They are hundreds of megabytes each
    /// and nothing reads them again — the version is baked into the path the loader searches. Takes the root
    /// rather than reaching for it, so what it is about to delete recursively is always the caller's to name.
    /// </summary>
    internal static void RemoveOtherVersions(string runtimesRoot, string keepVersion, ILogger? logger)
    {
        if (!Directory.Exists(runtimesRoot))
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(runtimesRoot))
        {
            var version = Path.GetFileName(directory);
            if (string.Equals(version, keepVersion, StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                Directory.Delete(directory, recursive: true);
                logger?.LogInformation("Removed cached Whisper runtimes for superseded version {Version}", version);
            }
            catch (Exception exception)
            {
                // Another cockpit still has the old natives mapped, or the disk says no. Reclaiming space is
                // housekeeping; failing it is not worth interrupting a dictation the operator is waiting on.
                logger?.LogDebug(exception, "Could not remove cached Whisper runtimes for version {Version}", version);
            }
        }
    }

    /// <summary>
    /// Which host the cockpit is on, or null on one Whisper.net publishes no runtimes for at all. The caller
    /// resolves this once and hands it to both the planner and the cache, so the order that gets tried and the
    /// runtime that gets fetched can never disagree about where they are.
    /// </summary>
    public static WhisperHostPlatform? CurrentPlatform =>
        OperatingSystem.IsWindows() ? WhisperHostPlatform.Windows
        : OperatingSystem.IsLinux() ? WhisperHostPlatform.Linux
        : OperatingSystem.IsMacOS() ? WhisperHostPlatform.MacOs
        : null;

    private static string? _CurrentArchitecture() => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => "x64",
        Architecture.X86 => "x86",
        Architecture.Arm => "arm",
        Architecture.Arm64 => "arm64",
        _ => null,
    };

    private static void _DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    /// <summary>
    /// Clears a scratch file or directory once the outcome is already decided. Best-effort by design: a locked
    /// temp file — Windows Defender scanning a freshly written DLL is routine — must never become the result,
    /// least of all of a fetch that succeeded, which a throwing <c>finally</c> would turn into a crash.
    /// </summary>
    private static void _DeleteQuietly(string path, ILogger? logger)
    {
        try
        {
            _DeleteDirectory(path);

            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception)
        {
            logger?.LogDebug(exception, "Could not clean up {Path} after a Whisper runtime fetch", path);
        }
    }
}
