using System.Text.Json;
using Cockpit.Plugins.Abstractions.ManagedCli;

namespace Cockpit.Plugin.Bitwarden;

// bws install recipe (modelled on Cockpit.Plugin.Kubernetes/Helm/HelmManagedCli.cs). Bitwarden publishes
// bws-sha256-checksums-<version>.txt with every release of github.com/bitwarden/sdk-sm; that repo also tags other
// products, so only `bws-v<semver>` tags count.
internal static class BitwardenManagedCli
{
    public const string CliName = "bws";

    private const string ReleasesApi = "https://api.github.com/repos/bitwarden/sdk-sm/releases?per_page=100";
    private const string DownloadBase = "https://github.com/bitwarden/sdk-sm/releases/download";

    public static ManagedCliDescriptor Descriptor { get; } = new()
    {
        CliName = CliName,
        ResolveLatestVersionAsync = async (http, cancellationToken) => ResolveLatestVersion(await _GetAsync(http, ReleasesApi, cancellationToken).ConfigureAwait(false)),
        BuildDownloadPlanAsync = BuildPlanAsync,
    };

    // Highest published bws-v<semver> release; other tags in the repo are ignored.
    internal static string ResolveLatestVersion(string releasesJson)
    {
        using var document = JsonDocument.Parse(releasesJson);
        Version? best = null;
        foreach (var release in document.RootElement.EnumerateArray())
        {
            if ((release.TryGetProperty("draft", out var draft) && draft.GetBoolean())
                || (release.TryGetProperty("prerelease", out var prerelease) && prerelease.GetBoolean()))
            {
                continue;
            }

            var tag = release.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() : null;
            if (tag is not null && tag.StartsWith("bws-v", StringComparison.Ordinal)
                && Version.TryParse(tag["bws-v".Length..], out var parsed) && (best is null || parsed > best))
            {
                best = parsed;
            }
        }

        return best?.ToString() ?? throw new InvalidOperationException("No published bws release was found.");
    }

    // Release asset target: macOS ships one universal build; Linux has gnu and musl builds.
    internal static string Target(ManagedCliPlatform platform) => (platform.Os, platform.Arch) switch
    {
        ("win32", "x64") => "x86_64-pc-windows-msvc",
        ("win32", "arm64") => "aarch64-pc-windows-msvc",
        ("darwin", _) => "macos-universal",
        ("linux", "x64") => platform.IsMusl ? "x86_64-unknown-linux-musl" : "x86_64-unknown-linux-gnu",
        ("linux", "arm64") => platform.IsMusl ? "aarch64-unknown-linux-musl" : "aarch64-unknown-linux-gnu",
        _ => throw new InvalidOperationException($"bws has no build for {platform.Os}/{platform.Arch}."),
    };

    internal static string AssetName(ManagedCliPlatform platform, string version) => $"bws-{Target(platform)}-{version}.zip";

    // Finds the hash for one file in "<hash>  <file>" lines.
    internal static string ParseChecksum(string checksumsText, string fileName)
    {
        foreach (var line in checksumsText.Split('\n'))
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && string.Equals(parts[1].TrimStart('*'), fileName, StringComparison.Ordinal))
            {
                return parts[0];
            }
        }

        throw new InvalidOperationException($"The checksum list has no entry for '{fileName}'.");
    }

    internal static async Task<ManagedCliDownloadPlan> BuildPlanAsync(HttpClient http, ManagedCliPlatform platform, string version, CancellationToken cancellationToken)
    {
        var assetName = AssetName(platform, version);
        var baseUrl = $"{DownloadBase}/bws-v{version}";
        var checksums = await _GetAsync(http, $"{baseUrl}/bws-sha256-checksums-{version}.txt", cancellationToken).ConfigureAwait(false);
        var executable = platform.Os == "win32" ? "bws.exe" : "bws";
        return new ManagedCliDownloadPlan
        {
            Url = $"{baseUrl}/{assetName}",
            ExpectedSha256 = ParseChecksum(checksums, assetName),
            ExecutableFileName = executable,
            ArchiveFormat = ManagedCliArchiveFormat.Zip,
            ExecutableEntryName = executable,
            NeedsExecutableBit = platform.Os != "win32",
        };
    }

    private static async Task<string> _GetAsync(HttpClient http, string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("Cockpit-ManagedCli");
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }
}
