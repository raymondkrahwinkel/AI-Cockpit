using System.Collections.Concurrent;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Plugins;
using Cockpit.Infrastructure.Configuration;
using Cockpit.Plugins.Abstractions.ManagedCli;
using Microsoft.Extensions.Logging;
using SharpCompress.Readers;

namespace Cockpit.Infrastructure.ManagedCli;

/// <summary>
/// The generic managed-CLI installer (AC-20). Holds the descriptors plugins register and turns any one of them into
/// an on-disk executable: resolve latest version → build the download plan → download → verify SHA-256 → unpack →
/// place atomically under <c>&lt;StateRoot&gt;/cli/&lt;name&gt;/&lt;version&gt;/</c>. Reuses the project's proven
/// building blocks — <see cref="PluginHash"/> for the checksum (same as the plugin store), the download-to-temp-then-
/// move discipline of the voice caches so a failed install never leaves a half copy, and SharpCompress for the same
/// tar extraction the voice caches do. Names no provider: Claude and Codex differ only in the descriptor they hand in.
/// </summary>
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
        if (string.IsNullOrWhiteSpace(cliName))
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
                var resolved = (await descriptor.ResolveLatestVersionAsync(_http, cancellationToken).ConfigureAwait(false)).Trim();
                // Only report a latest version that passes the same gate an install would — a garbage/edge response
                // must not present itself as an available update.
                if (Version.TryParse(resolved, out _))
                {
                    latest = resolved;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                // Offline or a channel hiccup — "can't tell", not a failure the caller has to handle.
                _logger?.LogDebug(exception, "Managed CLI '{CliName}' latest-version check failed", cliName);
            }
        }

        return new ManagedCliStatus(installed, latest);
    }

    private string? _InstalledVersion(string cliName)
    {
        if (string.IsNullOrWhiteSpace(cliName))
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
        if (string.IsNullOrWhiteSpace(cliName))
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

            // The version string comes from the provider's own channel (a compromised/edge-poisoned origin is exactly
            // the supply-chain threat this feature courts), and it becomes a path segment and a URL component. Require
            // it to be a plain dotted-numeric Version: that rejects any traversal (`..`, separators) outright, and it
            // is also the shape ResolveInstalledPath parses — so an install can never land in a directory resolution
            // would not find. Anything else is refused rather than trusted.
            if (!Version.TryParse(version, out _))
            {
                return ManagedCliInstallResult.Fail($"'{cliName}' reported an unexpected version format ('{version}') and was not installed.");
            }

            var versionDirectory = Path.Combine(_cliRoot, cliName, version);
            var plan = await descriptor.BuildDownloadPlanAsync(_http, platform, version, cancellationToken).ConfigureAwait(false);
            var finalPath = Path.Combine(versionDirectory, plan.ExecutableFileName);

            if (File.Exists(finalPath))
            {
                // Already on disk — a managed install is content-addressed by version, so re-fetching the same one
                // is wasted bytes. An update to a newer version is a separate, explicit EnsureInstalled of that version.
                return ManagedCliInstallResult.Ok(version, finalPath);
            }

            await _DownloadVerifyPlaceAsync(plan, versionDirectory, cancellationToken).ConfigureAwait(false);
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
        // Build in a sibling ".download" directory and swap it into place only once everything succeeded, so a failed
        // or cancelled install never leaves a partial version dir that a later ResolveInstalledPath treats as complete.
        var tempDirectory = versionDirectory + ".download";
        _DeleteDirectoryIfExists(tempDirectory);
        Directory.CreateDirectory(tempDirectory);

        try
        {
            var bytes = await _DownloadAsync(plan.Url, cancellationToken).ConfigureAwait(false);

            // Verify before anything is written out or unpacked. A mismatch means the bytes are not what the provider
            // published — reject and install nothing.
            var actualSha = PluginHash.Compute(bytes);
            if (!string.Equals(actualSha, plan.ExpectedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"The download did not match the published SHA-256 and was rejected (expected {plan.ExpectedSha256.Trim()}, got {actualSha}).");
            }

            var executablePath = Path.Combine(tempDirectory, plan.ExecutableFileName);
            switch (plan.ArchiveFormat)
            {
                case ManagedCliArchiveFormat.RawBinary:
                    await File.WriteAllBytesAsync(executablePath, bytes, cancellationToken).ConfigureAwait(false);
                    break;
                case ManagedCliArchiveFormat.TarGz:
                    _ExtractExecutableFromTarGz(bytes, plan, executablePath);
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported managed-CLI archive format '{plan.ArchiveFormat}'.");
            }

            if (plan.NeedsExecutableBit && !OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(executablePath, ExecutableMode);
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
    // already in memory, and a MemoryStream is seekable, so SharpCompress can sniff the format without the rewind
    // trouble a forward-only network stream causes.
    private static void _ExtractExecutableFromTarGz(byte[] archiveBytes, ManagedCliDownloadPlan plan, string executablePath)
    {
        using var archiveStream = new MemoryStream(archiveBytes, writable: false);
        using var reader = ReaderFactory.OpenReader(archiveStream, new ReaderOptions());
        while (reader.MoveToNextEntry())
        {
            if (reader.Entry.IsDirectory)
            {
                continue;
            }

            if (!_EntryMatches(reader.Entry.Key, plan.ExecutableEntryName))
            {
                continue;
            }

            using var entryStream = reader.OpenEntryStream();
            using var output = File.Create(executablePath);

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
            plan.ExecutableEntryName is { Length: > 0 } wanted
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
