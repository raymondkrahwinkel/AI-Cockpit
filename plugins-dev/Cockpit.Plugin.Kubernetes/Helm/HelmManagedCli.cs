using System.Text.Json;
using Cockpit.Plugins.Abstractions.ManagedCli;

namespace Cockpit.Plugin.Kubernetes.Helm;

// Helm's managed-CLI install recipe (AC-1061 fase 3, modelled on Cockpit.Plugin.CliAgentProvider/CodexManagedCli.cs).
// Route (verified against the live GitHub release list and get.helm.sh, 2026-08): helm publishes two trains at once
// (a v3.x and a v4.x release can both be current), and the more recently *published* one is not necessarily the
// higher version — so ResolveLatestVersionAsync lists releases, skips drafts/prereleases, and takes the highest
// semver rather than trusting publish order or /releases/latest. Downloads live at
// get.helm.sh/helm-<tag>-<os>-<arch>.<ext>, each with a sibling one-line "<hash>  <filename>" ".sha256sum" — fetched
// per version, so nothing needs to be known ahead of time. The download host is pinned to get.helm.sh over https so
// a spoofed releases response cannot redirect the fetch elsewhere.
internal static class HelmManagedCli
{
    public const string CliName = "helm";

    private const string ReleasesApiBase = "https://api.github.com/repos/helm/helm/releases";
    private const string DownloadHost = "get.helm.sh";

    public static ManagedCliDescriptor Descriptor { get; } = new()
    {
        CliName = CliName,
        ResolveLatestVersionAsync = async (http, cancellationToken) =>
        {
            var json = await _GetJsonAsync(http, $"{ReleasesApiBase}?per_page=30", cancellationToken).ConfigureAwait(false);
            return ResolveLatestVersion(json);
        },
        BuildDownloadPlanAsync = (http, platform, version, cancellationToken) => BuildPlanAsync(http, platform, version, cancellationToken),
    };

    // Picks the highest published, non-draft/non-prerelease semver from a GitHub releases-list response — never the
    // newest by publish date, since helm's two trains (v3.x, v4.x) publish independently. Internal for testing.
    internal static string ResolveLatestVersion(string releasesJson)
    {
        using var document = JsonDocument.Parse(releasesJson);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("The helm releases response was not a list.");
        }

        string? best = null;
        Version? bestVersion = null;
        foreach (var release in document.RootElement.EnumerateArray())
        {
            if ((release.TryGetProperty("draft", out var draft) && draft.GetBoolean())
                || (release.TryGetProperty("prerelease", out var prerelease) && prerelease.GetBoolean()))
            {
                continue;
            }

            var tag = release.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() : null;
            if (string.IsNullOrEmpty(tag))
            {
                continue;
            }

            var candidate = ParseVersion(tag);
            if (Version.TryParse(candidate, out var parsed) && (bestVersion is null || parsed > bestVersion))
            {
                bestVersion = parsed;
                best = candidate;
            }
        }

        return best ?? throw new InvalidOperationException("No published helm release was found.");
    }

    // `v4.2.4` → `4.2.4`. Internal for testing.
    internal static string ParseVersion(string tagName) =>
        tagName.StartsWith('v') ? tagName[1..] : tagName;

    // get.helm.sh's OS key — win32 becomes "windows", the rest are unchanged. Internal for testing.
    internal static string TargetOs(ManagedCliPlatform platform) =>
        platform.Os == "win32" ? "windows" : platform.Os;

    // get.helm.sh's arch key. Internal for testing.
    internal static string TargetArch(ManagedCliPlatform platform) =>
        platform.Arch switch
        {
            "x64" => "amd64",
            "arm64" => "arm64",
            _ => throw new InvalidOperationException($"Helm has no build for architecture '{platform.Arch}'."),
        };

    // The asset name for a platform + version — Windows ships a .zip, everything else a .tar.gz. Internal for testing.
    internal static string AssetName(ManagedCliPlatform platform, string version)
    {
        var ext = platform.Os == "win32" ? "zip" : "tar.gz";
        return $"helm-v{version}-{TargetOs(platform)}-{TargetArch(platform)}.{ext}";
    }

    // The archive entry the tarball/zip unpacks to — helm nests it under "<os>-<arch>/". Internal for testing.
    internal static string EntryName(ManagedCliPlatform platform) =>
        platform.Os == "win32" ? $"{TargetOs(platform)}-{TargetArch(platform)}/helm.exe" : $"{TargetOs(platform)}-{TargetArch(platform)}/helm";

    // Parses a "<hash>  <filename>" .sha256sum line and returns the hash. Internal for testing.
    internal static string ParseChecksum(string sha256sumText, string expectedFileName)
    {
        var firstLine = sha256sumText.AsSpan().TrimStart().ToString().Split('\n')[0].Trim();
        var parts = firstLine.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !string.Equals(parts[1], expectedFileName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"The .sha256sum for '{expectedFileName}' had an unexpected shape ('{sha256sumText.Trim()}').");
        }

        return parts[0];
    }

    // Builds the download plan for a platform + resolved version: the asset URL on get.helm.sh, plus one extra GET
    // for its sibling .sha256sum. Internal for testing via the http client a test can stub.
    internal static async Task<ManagedCliDownloadPlan> BuildPlanAsync(HttpClient http, ManagedCliPlatform platform, string version, CancellationToken cancellationToken)
    {
        var assetName = AssetName(platform, version);
        var url = $"https://{DownloadHost}/{assetName}";
        if (!_IsTrustedDownloadUrl(url))
        {
            throw new InvalidOperationException($"The helm asset '{assetName}' has an untrusted download URL ('{url}') and was refused.");
        }

        var checksumText = await _GetTextAsync(http, $"{url}.sha256sum", cancellationToken).ConfigureAwait(false);
        var isWindows = platform.Os == "win32";
        return new ManagedCliDownloadPlan
        {
            Url = url,
            ExpectedSha256 = ParseChecksum(checksumText, assetName),
            ExecutableFileName = isWindows ? "helm.exe" : "helm",
            ArchiveFormat = isWindows ? ManagedCliArchiveFormat.Zip : ManagedCliArchiveFormat.TarGz,
            ExecutableEntryName = EntryName(platform),
            NeedsExecutableBit = !isWindows,
        };
    }

    // An https download from get.helm.sh only — a spoofed/edge releases response must not be able to point the
    // fetch (or the checksum fetch) at an arbitrary host.
    private static bool _IsTrustedDownloadUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.Host == DownloadHost;

    // GitHub's API rejects requests without a User-Agent; send one and ask for the documented REST media type.
    private static async Task<string> _GetJsonAsync(HttpClient http, string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("Cockpit-ManagedCli");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> _GetTextAsync(HttpClient http, string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("Cockpit-ManagedCli");
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }
}
