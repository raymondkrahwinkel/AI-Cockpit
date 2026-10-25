using System.Diagnostics;
using System.Formats.Tar;
using System.Security.Cryptography;
using Cockpit.Core.Configuration;
using SharpCompress.Compressors.BZip2;

namespace Cockpit.Plugin.Browser.UI.Runtime;

internal sealed record CefRuntimeProgress(string Phase, long Done, long? Total);

// Directory is the runtime to load, or null with the reason in Failure. A refusal is a result, not an
// exception: the panel shows the reason and the browser stays off (AC-1727).
internal sealed record CefRuntimeResult(string? Directory, string? Failure);

// Fetches the CEF runtime on first use (AC-1727), after `WhisperRuntimeCache`. Unlike Whisper's, the archive is
// checked against a pinned SHA-256 before it is unpacked, and a failure is returned instead of falling back:
// there is no bundled CEF to fall back to.
internal sealed class CefRuntimeCache
{
    private static readonly HttpClient SharedClient = new() { Timeout = TimeSpan.FromMinutes(2) };

    private static readonly TimeSpan ReportInterval = TimeSpan.FromMilliseconds(250);

    private readonly string _root;
    private readonly HttpClient _http;

    public CefRuntimeCache(string root, HttpClient? http = null)
    {
        _root = root;
        _http = http ?? SharedClient;
    }

    public static string DefaultRoot => Path.Combine(CockpitBuild.StateRoot, "browser-runtime");

    public async Task<CefRuntimeResult> EnsureAvailableAsync(
        CefRuntimeCatalog catalog,
        string rid,
        IProgress<CefRuntimeProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!catalog.Packages.TryGetValue(rid, out var package))
        {
            return new CefRuntimeResult(null, $"CEF {catalog.CefVersion} has no build for {rid}.");
        }

        var versionRoot = Path.Combine(_root, catalog.CefVersion);
        var destination = Path.Combine(versionRoot, rid);

        if (!_IsComplete(destination, rid))
        {
            var failure = await _FetchAsync(package, rid, versionRoot, destination, progress, cancellationToken)
                .ConfigureAwait(false);
            if (failure is not null)
            {
                return new CefRuntimeResult(null, failure);
            }
        }

        // Only a verified, whole runtime may displace the older ones.
        RemoveOtherVersions(_root, catalog.CefVersion);

        return new CefRuntimeResult(destination, null);
    }

    // Scratch paths carry the process id so two cockpits warming a cold cache cannot trample each other.
    private async Task<string?> _FetchAsync(
        CefRuntimePackage package,
        string rid,
        string versionRoot,
        string destination,
        IProgress<CefRuntimeProgress>? progress,
        CancellationToken cancellationToken)
    {
        var archive = Path.Combine(versionRoot, $"{rid}.{Environment.ProcessId}.download");
        var staging = $"{destination}.{Environment.ProcessId}.fetching";

        try
        {
            Directory.CreateDirectory(versionRoot);

            var actual = await _DownloadAsync(package, archive, progress, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(actual, package.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return $"The downloaded CEF runtime does not match its pinned checksum (expected {package.Sha256[..12]}…, got {actual[..12]}…) and was discarded.";
            }

            progress?.Report(new CefRuntimeProgress("Unpacking", 0, null));
            _DeleteDirectory(staging);
            Directory.CreateDirectory(staging);
            Extract(archive, staging);
            if (!_IsComplete(staging, rid))
            {
                return "The CEF archive did not contain the runtime library for this platform.";
            }

            // Swapped in only once whole: an interrupted fetch never leaves a directory the next run reads as cached.
            _DeleteDirectory(destination);
            Directory.Move(staging, destination);

            return null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return $"The CEF runtime could not be fetched: {exception.Message}";
        }
        finally
        {
            _DeleteQuietly(staging);
            _DeleteQuietly(archive);
        }
    }

    // Writes the archive and hashes it in the same pass. Stops at the pinned size, so a server that never
    // stops sending cannot fill the disk.
    private async Task<string> _DownloadAsync(
        CefRuntimePackage package,
        string archive,
        IProgress<CefRuntimeProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await _http
            .GetAsync(package.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var target = File.Create(archive);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        var buffer = new byte[81920];
        var copied = 0L;
        var sinceLastReport = Stopwatch.StartNew();
        progress?.Report(new CefRuntimeProgress("Downloading", 0, package.Size));

        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            copied += read;
            if (copied > package.Size)
            {
                throw new InvalidDataException($"The download is larger than the pinned {package.Size} bytes.");
            }

            hash.AppendData(buffer, 0, read);
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);

            if (sinceLastReport.Elapsed >= ReportInterval)
            {
                progress?.Report(new CefRuntimeProgress("Downloading", copied, package.Size));
                sinceLastReport.Restart();
            }
        }

        progress?.Report(new CefRuntimeProgress("Downloading", copied, package.Size));

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    // Flattens `Release/*` and `Resources/*` of the minimal distribution into `staging`, leaving out what only a
    // build needs (import library, bootstrap executables). Only paths under staging are ever written.
    internal static void Extract(string archive, string staging)
    {
        using var file = File.OpenRead(archive);
        using var bzip2 = BZip2Stream.Create(file, SharpCompress.Compressors.CompressionMode.Decompress, false);
        using var tar = new TarReader(bzip2);
        var stagingPrefix = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;

        while (tar.GetNextEntry() is { } entry)
        {
            // <top directory>/<Release|Resources>/<path inside the runtime>
            var parts = entry.Name.Split('/', 3);
            if (parts.Length < 3 || parts[2].Length == 0 || parts[1] is not ("Release" or "Resources") || _IsBuildOnly(parts[2]))
            {
                continue;
            }

            var target = Path.GetFullPath(Path.Combine(staging, parts[2]));
            if (!target.StartsWith(stagingPrefix, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"The archive entry {entry.Name} points outside the runtime directory.");
            }

            if (entry.EntryType == TarEntryType.Directory)
            {
                Directory.CreateDirectory(target);
                continue;
            }

            var parent = Path.GetDirectoryName(target) ?? staging;
            Directory.CreateDirectory(parent);

            // A framework's symlinks must stay inside it: a link that resolves elsewhere is an escape.
            if (entry.EntryType == TarEntryType.SymbolicLink
                && !Path.GetFullPath(Path.Combine(parent, entry.LinkName)).StartsWith(stagingPrefix, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"The archive link {entry.Name} points outside the runtime directory.");
            }

            entry.ExtractToFile(target, overwrite: true);
        }
    }

    // Keeps only the runtime of the version we load: the others are hundreds of megabytes each and nothing
    // reads them again. A runtime another cockpit still has mapped refuses to go and is left for next time.
    internal static void RemoveOtherVersions(string root, string keepVersion)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            if (!string.Equals(Path.GetFileName(directory), keepVersion, StringComparison.Ordinal))
            {
                _DeleteQuietly(directory);
            }
        }
    }

    // The library the loader opens first; its presence in a directory that was moved in whole means "cached".
    private static bool _IsComplete(string directory, string rid) => rid.StartsWith("osx", StringComparison.Ordinal)
        ? Directory.Exists(Path.Combine(directory, "Chromium Embedded Framework.framework"))
        : File.Exists(Path.Combine(directory, rid.StartsWith("win", StringComparison.Ordinal) ? "libcef.dll" : "libcef.so"));

    private static bool _IsBuildOnly(string path) =>
        !path.Contains('/')
        && (path.EndsWith(".lib", StringComparison.OrdinalIgnoreCase)
            || (path.StartsWith("bootstrap", StringComparison.OrdinalIgnoreCase) && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)));

    private static void _DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    // Best-effort: a locked file (a scanner on a fresh DLL, another cockpit) must not become the result.
    private static void _DeleteQuietly(string path)
    {
        try
        {
            _DeleteDirectory(path);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
