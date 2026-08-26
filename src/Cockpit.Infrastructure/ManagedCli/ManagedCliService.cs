using System.Collections.Concurrent;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Plugins;
using Cockpit.Infrastructure.Configuration;
using Cockpit.Plugins.Abstractions.ManagedCli;
using Microsoft.Extensions.Logging;
using SharpCompress.Readers;

namespace Cockpit.Infrastructure.ManagedCli;

// The generic managed-CLI installer (AC-20): resolve latest version → build download plan → download → verify
// SHA-256 → unpack → place atomically under `&lt;StateRoot&gt;/cli/&lt;name&gt;/&lt;version&gt;/`. Reuses
// `PluginHash` and the voice caches' download-to-temp-then-move discipline. Names no provider.
internal sealed class ManagedCliService : IManagedCliService, ISingletonService
{
    // Owner rwx + group/other rx (0755) — a launcher the user runs; mirrors what the official installers set.
    private const UnixFileMode ExecutableMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    // Headroom over the largest real CLI (claude ~264 MB) — a cap so a compromised/misbehaving origin cannot stream an
    // unbounded body into memory (OOM) before the checksum is even computed, and a decompression bomb cannot fill the disk.
    private const long MaxDownloadBytes = 600L * 1024 * 1024;

    // One shared client for the process, same rationale as the voice caches and the plugin store: avoid per-download
    // socket exhaustion. Overridable through the internal constructor so a test can supply a stubbed handler.
    private static readonly HttpClient SharedHttp = new();

    private readonly ConcurrentDictionary<string, ManagedCliDescriptor> _descriptors = new(StringComparer.Ordinal);
    private readonly string _cliRoot;
    private readonly HttpClient _http;
    private readonly ILogger<ManagedCliService>? _logger;

    public ManagedCliService(ILogger<ManagedCliService> logger)
        : this(null, null, logger)
    {
    }

    internal ManagedCliService(string? cliRoot, HttpClient? http, ILogger<ManagedCliService>? logger)
    {
        _cliRoot = cliRoot ?? Path.Combine(CockpitConfigPath.Root, "cli");
        _http = http ?? SharedHttp;
        _logger = logger;
    }

    public IReadOnlyCollection<string> RegisteredCliNames => [.. _descriptors.Keys];

    public void Register(ManagedCliDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        _descriptors[descriptor.CliName] = descriptor;
    }

    public string? ResolveInstalledPath(string cliName)
    {
        if (!_IsSafeCliName(cliName))
        {
            return null;
        }

        var cliDirectory = Path.Combine(_cliRoot, cliName);
        if (!Directory.Exists(cliDirectory))
        {
            return null;
        }

        var newestVersionDirectory = _NewestVersionDirectory(cliDirectory);
        return newestVersionDirectory is null ? null : _ExecutableIn(newestVersionDirectory, cliName);
    }

    public async Task<ManagedCliStatus> GetStatusAsync(string cliName, CancellationToken cancellationToken = default)
    {
        var installed = _InstalledVersion(cliName);

        string? latest = null;
        if (_descriptors.TryGetValue(cliName, out var descriptor))
        {
            try
            {
                // Bound the check so a hung endpoint cannot hold the config-view button on "Checking…" or the
                // 15-minute poller for the HttpClient default (100 s) — the download path caps itself the same way.
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));

                var resolved = (await descriptor.ResolveLatestVersionAsync(_http, timeout.Token).ConfigureAwait(false)).Trim();
                // Only report a latest version that passes the same gate an install would — a garbage/edge response
                // must not present itself as an available update.
                if (Version.TryParse(resolved, out _))
                {
                    latest = resolved;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw; // the caller cancelled — propagate; the internal 20 s timeout falls through to "can't tell" below
            }
            catch (Exception exception)
            {
                // Offline, a slow endpoint hitting the 20 s cap, or an edge response — "can't tell", not a failure.
                _logger?.LogDebug(exception, "Managed CLI '{CliName}' latest-version check failed", cliName);
            }
        }

        return new ManagedCliStatus(installed, latest);
    }

    private string? _InstalledVersion(string cliName)
    {
        if (!_IsSafeCliName(cliName))
        {
            return null;
        }

        var cliDirectory = Path.Combine(_cliRoot, cliName);
        if (!Directory.Exists(cliDirectory))
        {
            return null;
        }

        var newest = _NewestVersionDirectory(cliDirectory);
        return newest is null ? null : Path.GetFileName(newest);
    }

    public bool RemoveInstalled(string cliName)
    {
        if (!_IsSafeCliName(cliName))
        {
            return false;
        }

        var cliDirectory = Path.Combine(_cliRoot, cliName);
        if (!Directory.Exists(cliDirectory))
        {
            return false;
        }

        try
        {
            Directory.Delete(cliDirectory, recursive: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(exception, "Could not remove managed CLI '{CliName}'", cliName);
            return false;
        }
    }

    public async Task<ManagedCliInstallResult> EnsureInstalledAsync(string cliName, CancellationToken cancellationToken = default)
    {
        if (!_IsSafeCliName(cliName))
        {
            return ManagedCliInstallResult.Fail($"'{cliName}' is not a valid managed-CLI name.");
        }

        if (!_descriptors.TryGetValue(cliName, out var descriptor))
        {
            return ManagedCliInstallResult.Fail($"No managed-CLI descriptor is registered for '{cliName}'.");
        }

        try
        {
            var platform = ManagedCliPlatform.Current();
            var version = (await descriptor.ResolveLatestVersionAsync(_http, cancellationToken).ConfigureAwait(false)).Trim();
            if (string.IsNullOrEmpty(version))
            {
                return ManagedCliInstallResult.Fail($"Could not determine the latest version of '{cliName}'.");
            }

            // The version string comes from the provider's channel and becomes a path segment and URL component,
            // so a compromised origin is a supply-chain threat. Require a plain dotted-numeric Version: rejects
            // traversal outright, and matches the shape ResolveInstalledPath parses. Anything else is refused.
            if (!Version.TryParse(version, out _))
            {
                return ManagedCliInstallResult.Fail($"'{cliName}' reported an unexpected version format ('{version}') and was not installed.");
            }

            var versionDirectory = Path.Combine(_cliRoot, cliName, version);
            var plan = await descriptor.BuildDownloadPlanAsync(_http, platform, version, cancellationToken).ConfigureAwait(false);
            var finalPath = Path.Combine(versionDirectory, plan.ExecutableFileName);

            // A managed install is content-addressed by version, so every expected file already on disk is wasted
            // bytes to re-fetch — but a recipe that now promises a sibling binary (AC-1107) it did not before must
            // still be topped up even when the primary executable is already there. An update to a newer version is
            // a separate, explicit EnsureInstalled of that version.
            var missingArtifacts = _AllArtifacts(plan)
                .Where(artifact => !File.Exists(Path.Combine(versionDirectory, artifact.FileName)))
                .ToList();

            if (missingArtifacts.Count == 0)
            {
                _CleanupOldVersions(cliName, version);
                return ManagedCliInstallResult.Ok(version, finalPath);
            }

            if (Directory.Exists(versionDirectory))
            {
                // A partial version directory (a sibling asset missing entirely, or a previous repair pass that got
                // interrupted) — top up only what is missing rather than re-downloading what is already verified.
                foreach (var artifact in missingArtifacts)
                {
                    await _DownloadVerifyPlaceArtifactAsync(artifact, versionDirectory, cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                await _DownloadVerifyPlaceAsync(plan, versionDirectory, cancellationToken).ConfigureAwait(false);
            }

            _CleanupOldVersions(cliName, version);
            return ManagedCliInstallResult.Ok(version, finalPath);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger?.LogWarning(exception, "Managed CLI '{CliName}' install failed", cliName);
            return ManagedCliInstallResult.Fail($"Could not install '{cliName}': {exception.Message}");
        }
    }

    private async Task _DownloadVerifyPlaceAsync(ManagedCliDownloadPlan plan, string versionDirectory, CancellationToken cancellationToken)
    {
        // Build in a sibling ".download" directory and swap it into place only once every artifact succeeded, so a
        // failed or cancelled install never leaves a partial version dir that a later ResolveInstalledPath treats as
        // complete. (A version dir that already exists but is missing an artifact takes the repair path in
        // EnsureInstalledAsync instead, writing straight into it — see _DownloadVerifyPlaceArtifactAsync.)
        var tempDirectory = versionDirectory + ".download";
        _DeleteDirectoryIfExists(tempDirectory);
        Directory.CreateDirectory(tempDirectory);

        try
        {
            foreach (var artifact in _AllArtifacts(plan))
            {
                await _DownloadVerifyPlaceArtifactAsync(artifact, tempDirectory, cancellationToken).ConfigureAwait(false);
            }

            var parent = Path.GetDirectoryName(versionDirectory);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            _DeleteDirectoryIfExists(versionDirectory);
            Directory.Move(tempDirectory, versionDirectory);
        }
        catch
        {
            _DeleteDirectoryIfExists(tempDirectory);
            throw;
        }
    }

    // Downloads, verifies and places one artifact (the plan's primary executable or one of its AdditionalArtifacts)
    // into destinationDirectory. Writes to a ".part" staging path and renames into place last, so a failure here
    // never corrupts an artifact that was already placed by an earlier pass (the repair path relies on this: other
    // files in destinationDirectory are untouched while this one is being fetched).
    private async Task _DownloadVerifyPlaceArtifactAsync(ManagedCliArtifactPlan artifact, string destinationDirectory, CancellationToken cancellationToken)
    {
        var bytes = await _DownloadAsync(artifact.Url, cancellationToken).ConfigureAwait(false);

        // Verify before anything is written out or unpacked. A mismatch means the bytes are not what the provider
        // published — reject and install nothing.
        var actualSha = PluginHash.Compute(bytes);
        if (!string.Equals(actualSha, artifact.ExpectedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"The download of '{artifact.FileName}' did not match the published SHA-256 and was rejected (expected {artifact.ExpectedSha256.Trim()}, got {actualSha}).");
        }

        var finalPath = Path.Combine(destinationDirectory, artifact.FileName);
        var stagingPath = finalPath + ".part";
        switch (artifact.ArchiveFormat)
        {
            case ManagedCliArchiveFormat.RawBinary:
                await File.WriteAllBytesAsync(stagingPath, bytes, cancellationToken).ConfigureAwait(false);
                break;
            case ManagedCliArchiveFormat.TarGz:
            case ManagedCliArchiveFormat.Zip:
                _ExtractArchiveEntry(bytes, artifact.ArchiveEntryName, stagingPath);
                break;
            default:
                throw new InvalidOperationException($"Unsupported managed-CLI archive format '{artifact.ArchiveFormat}'.");
        }

        if (artifact.NeedsExecutableBit && !OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(stagingPath, ExecutableMode);
        }

        File.Move(stagingPath, finalPath, overwrite: true);
    }

    // The plan's primary executable and its AdditionalArtifacts, as one uniform sequence — every consumer below
    // (download-all, missing-file detection) treats them identically; only the fetch source differs.
    private static IEnumerable<ManagedCliArtifactPlan> _AllArtifacts(ManagedCliDownloadPlan plan)
    {
        yield return new ManagedCliArtifactPlan(
            plan.Url, plan.ExpectedSha256, plan.ExecutableFileName, plan.ArchiveFormat, plan.ExecutableEntryName, plan.NeedsExecutableBit);

        foreach (var artifact in plan.AdditionalArtifacts)
        {
            yield return new ManagedCliArtifactPlan(
                artifact.Url, artifact.ExpectedSha256, artifact.FileName, artifact.ArchiveFormat, artifact.ArchiveEntryName, artifact.NeedsExecutableBit);
        }
    }

    // A uniform view over the plan's primary executable and its ManagedCliDownloadArtifact siblings, so the
    // download/verify/place code below does not need two near-identical code paths.
    private readonly record struct ManagedCliArtifactPlan(
        string Url,
        string ExpectedSha256,
        string FileName,
        ManagedCliArchiveFormat ArchiveFormat,
        string? ArchiveEntryName,
        bool NeedsExecutableBit);

    // Fetch the binary/archive bytes with a size cap, a timeout and a User-Agent. Streams the body and aborts the
    // moment it passes the cap, so an oversized (declared or actual) response never fully materialises in memory.
    private async Task<byte[]> _DownloadAsync(string url, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("Cockpit-ManagedCli");

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength is { } declared && declared > MaxDownloadBytes)
        {
            throw new InvalidOperationException($"The download is larger than the {MaxDownloadBytes / (1024 * 1024)} MB limit and was refused.");
        }

        await using var body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await body.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxDownloadBytes)
            {
                throw new InvalidOperationException($"The download exceeded the {MaxDownloadBytes / (1024 * 1024)} MB limit and was refused.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    // Curated extraction (the poison-bug lesson: take only what is needed, not a whole tree). The archive bytes are
    // already in memory, and a MemoryStream is seekable, so SharpCompress can sniff the format (tar.gz or zip)
    // without the rewind trouble a forward-only network stream causes — one reader serves both archive formats.
    private static void _ExtractArchiveEntry(byte[] archiveBytes, string? entryName, string outputPath)
    {
        using var archiveStream = new MemoryStream(archiveBytes, writable: false);
        using var reader = ReaderFactory.OpenReader(archiveStream, new ReaderOptions());
        while (reader.MoveToNextEntry())
        {
            if (reader.Entry.IsDirectory)
            {
                continue;
            }

            if (!_EntryMatches(reader.Entry.Key, entryName))
            {
                continue;
            }

            using var entryStream = reader.OpenEntryStream();
            using var output = File.Create(outputPath);

            // Bound the extracted size too: the archive passed the checksum, but cap defensively so a (source-signed)
            // decompression bomb cannot fill the disk.
            var chunk = new byte[81920];
            long written = 0;
            int read;
            while ((read = entryStream.Read(chunk, 0, chunk.Length)) > 0)
            {
                written += read;
                if (written > MaxDownloadBytes)
                {
                    throw new InvalidOperationException($"The archive entry exceeded the {MaxDownloadBytes / (1024 * 1024)} MB limit and was refused.");
                }

                output.Write(chunk, 0, read);
            }

            return;
        }

        throw new InvalidOperationException(
            entryName is { Length: > 0 } wanted
                ? $"The archive did not contain the expected entry '{wanted}'."
                : "The archive contained no file to extract.");
    }

    // With an entry name, match it by full key or by leaf name (the archive may nest the binary under a folder). With
    // none, take the first file — the single-file archive case.
    private static bool _EntryMatches(string? entryKey, string? wanted)
    {
        if (string.IsNullOrEmpty(wanted))
        {
            return true;
        }

        if (string.IsNullOrEmpty(entryKey))
        {
            return false;
        }

        return string.Equals(entryKey, wanted, StringComparison.Ordinal)
            || string.Equals(_LeafName(entryKey), _LeafName(wanted), StringComparison.Ordinal);
    }

    private static string _LeafName(string path) => path.Replace('\\', '/').TrimEnd('/').Split('/').Last();

    // A cli name becomes a path segment (<cliRoot>/<name>/...), so reject anything that could climb out of the cli
    // root — a separator or a dot-segment. Names come from a trusted in-process plugin, but the host API also takes a
    // caller-supplied name, so guard at every path-building entry rather than assume the caller sanitised it.
    private static bool _IsSafeCliName(string cliName) =>
        !string.IsNullOrWhiteSpace(cliName)
        && cliName.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) < 0
        && cliName != "."
        && cliName != "..";

    // After a successful install, a managed CLI keeps only its current version on disk — otherwise auto-update
    // (AC-767) grows the cli root unboundedly (~264 MB per claude version). Best-effort: a version directory
    // still open on Windows fails to delete (IOException) and is left for a later retry, not an error.
    private void _CleanupOldVersions(string cliName, string keepVersion)
    {
        var cliDirectory = Path.Combine(_cliRoot, cliName);
        if (!Directory.Exists(cliDirectory))
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(cliDirectory))
        {
            if (string.Equals(Path.GetFileName(directory), keepVersion, StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger?.LogDebug(exception, "Could not remove old version directory '{Directory}' of managed CLI '{CliName}'; will retry next pass.", directory, cliName);
            }
        }
    }

    private static string? _NewestVersionDirectory(string cliDirectory)
    {
        string? newestDirectory = null;
        Version? newestVersion = null;
        foreach (var directory in Directory.EnumerateDirectories(cliDirectory))
        {
            if (Version.TryParse(Path.GetFileName(directory), out var version)
                && (newestVersion is null || version > newestVersion))
            {
                newestVersion = version;
                newestDirectory = directory;
            }
        }

        return newestDirectory;
    }

    // A curated install holds just the executable, so prefer the conventional name and fall back to the sole file.
    private static string? _ExecutableIn(string versionDirectory, string cliName)
    {
        string[] preferredNames = [cliName, cliName + ".exe"];
        foreach (var name in preferredNames)
        {
            var candidate = Path.Combine(versionDirectory, name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        var files = Directory.EnumerateFiles(versionDirectory).Take(2).ToList();
        return files.Count == 1 ? files[0] : null;
    }

    private static void _DeleteDirectoryIfExists(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
