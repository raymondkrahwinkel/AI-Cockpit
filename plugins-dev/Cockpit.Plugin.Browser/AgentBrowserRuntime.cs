using System.IO.Compression;
using System.Security.Cryptography;
using System.Runtime.InteropServices;

namespace Cockpit.Plugin.Browser;

// AC-1712: Where the agent Chromium comes from. Playwright's `chromium-headless-shell` at a pinned version, fetched on
// first need and cached under the state root like the Whisper runtimes (docs/binary-size-and-on-demand-runtimes.md),
// with the system Edge/Chrome as the floor. Never bundled.
internal sealed class AgentBrowserRuntime(string cacheRoot, HttpClient http)
{
    // The `browserVersion` of chromium-headless-shell in playwright-core 1.64.1 (browsers.json, revision 1248).
    internal const string PinnedVersion = "156.0.8078.4";

    private const string CdnRoot = "https://cdn.playwright.dev/builds/cft";

    private static readonly SemaphoreSlim FetchGate = new(1, 1);

    // The pinned shell if it is cached, else a system browser, else null. Never waits on the network: a launch is
    // waiting on this, so a missing runtime is fetched in the background and the next session gets it.
    public string? Resolve(Action<Exception>? onFetchFailed = null)
    {
        // On Linux the packaged Chrome first: Ubuntu 23.10+ lets only the browsers it ships an AppArmor profile for create
        // the user namespaces the sandbox needs, so the downloaded shell dies there with "No usable sandbox".
        if (OperatingSystem.IsLinux() && SystemBrowser() is { } packaged)
        {
            return packaged;
        }

        if (CachedExecutable() is { } cached)
        {
            return cached;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await FetchAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                onFetchFailed?.Invoke(exception);
            }
        });

        return SystemBrowser();
    }

    public string? CachedExecutable()
    {
        var target = Target();
        var path = target is null ? null : Path.Combine(VersionRoot, target.Value.Folder, target.Value.Executable);
        return path is not null && File.Exists(path) ? path : null;
    }

    public async Task FetchAsync(CancellationToken cancellationToken)
    {
        if (Target() is not { } target || CachedExecutable() is not null)
        {
            return;
        }

        await FetchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (CachedExecutable() is not null)
            {
                return;
            }

            Directory.CreateDirectory(VersionRoot);

            // Scratch paths carry the process id so two cockpits warming a cold cache cannot trample each other.
            var archive = Path.Combine(VersionRoot, $"{target.Folder}.{Environment.ProcessId}.zip.download");
            var staging = Path.Combine(VersionRoot, $"{target.Folder}.{Environment.ProcessId}.fetching");
            try
            {
                using (var response = await http.GetAsync($"{CdnRoot}/{PinnedVersion}/{target.Archive}", HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    await using var file = File.Create(archive);
                    await source.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
                }

                await using (var downloaded = File.OpenRead(archive))
                {
                    var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(downloaded, cancellationToken).ConfigureAwait(false));
                    if (!string.Equals(actual, target.Sha256, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException($"The Chromium archive does not match the pinned SHA-256 (got {actual}); refusing it.");
                    }
                }

                // Swapped in whole, so an interrupted fetch never leaves a half-filled folder read as a cached runtime.
                DeleteDirectory(staging);
                ZipFile.ExtractToDirectory(archive, staging);
                var executable = Path.Combine(staging, target.Folder, target.Executable);
                if (!File.Exists(executable))
                {
                    throw new InvalidOperationException($"The Chromium archive carried no {target.Folder}/{target.Executable}.");
                }

                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(executable, File.GetUnixFileMode(executable) | UnixFileMode.UserExecute);
                }

                var destination = Path.Combine(VersionRoot, target.Folder);
                DeleteDirectory(destination);
                Directory.Move(Path.Combine(staging, target.Folder), destination);
            }
            finally
            {
                DeleteDirectory(staging);
                if (File.Exists(archive))
                {
                    File.Delete(archive);
                }
            }

            RemoveOtherVersions();
        }
        finally
        {
            FetchGate.Release();
        }
    }

    // The first system browser that can run headless: Chrome first (the same engine family), then Edge.
    internal static string? SystemBrowser() => SystemCandidates().FirstOrDefault(File.Exists);

    private static IEnumerable<string> SystemCandidates()
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (var root in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.LocalApplicationData })
            {
                var folder = Environment.GetFolderPath(root);
                yield return Path.Combine(folder, "Google", "Chrome", "Application", "chrome.exe");
                yield return Path.Combine(folder, "Microsoft", "Edge", "Application", "msedge.exe");
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome";
            yield return "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge";
        }
        else
        {
            foreach (var name in new[] { "google-chrome", "google-chrome-stable", "chromium", "chromium-browser", "microsoft-edge" })
            {
                foreach (var directory in new[] { "/usr/bin", "/usr/local/bin", "/snap/bin", "/opt/google/chrome" })
                {
                    yield return Path.Combine(directory, name);
                }
            }
        }
    }

    private string VersionRoot => Path.Combine(cacheRoot, PinnedVersion);

    // What Playwright's own registry publishes for the headless shell; a platform it does not list has no cached
    // runtime and rides on the system browser.
    private static (string Archive, string Folder, string Executable, string Sha256)? Target()
    {
        var arm = RuntimeInformation.OSArchitecture == Architecture.Arm64;
        if (OperatingSystem.IsWindows())
        {
            return arm ? null : ("win64/chrome-headless-shell-win64.zip", "chrome-headless-shell-win64", "chrome-headless-shell.exe", "5af4c82cf4b351ec4bad4c71dcf938af14fccea8a0eb0d5c2a7b49ccc922698c");
        }

        if (OperatingSystem.IsMacOS())
        {
            return arm
                ? ("mac-arm64/chrome-headless-shell-mac-arm64.zip", "chrome-headless-shell-mac-arm64", "chrome-headless-shell", "94e4337b70cbc5638f0935fbc46b9f242bc7416f657e3fbe924baf348404b53d")
                : ("mac-x64/chrome-headless-shell-mac-x64.zip", "chrome-headless-shell-mac-x64", "chrome-headless-shell", "8a9e4a32ba5b1a796f8b16d01896e355cf6662544d9743296394fd999d782331");
        }

        return arm
            ? ("linux-arm64/chrome-headless-shell-linux-arm64.zip", "chrome-headless-shell-linux-arm64", "chrome-headless-shell", "81ab9d4d3c301dc5fdfb6c541b9e6632b16fc819a6a854460f40a76fe2b2c476")
            : ("linux64/chrome-headless-shell-linux64.zip", "chrome-headless-shell-linux64", "chrome-headless-shell", "c3e50596aef52c58f8fda3c6b3411c33d34c88b177b3ed1c2243359fd3b8ba20");
    }

    // The version is in the path, so a bump needs no migration; the superseded ones are given back afterwards.
    private void RemoveOtherVersions()
    {
        foreach (var directory in Directory.EnumerateDirectories(cacheRoot))
        {
            if (!string.Equals(Path.GetFileName(directory), PinnedVersion, StringComparison.Ordinal))
            {
                DeleteDirectory(directory);
            }
        }
    }

    private static void DeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A locked leftover is tried again on the next fetch.
        }
    }
}
