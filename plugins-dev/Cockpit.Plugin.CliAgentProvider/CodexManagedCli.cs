using System.Text.Json;
using Cockpit.Plugins.Abstractions.ManagedCli;

namespace Cockpit.Plugin.CliAgentProvider;

/// <summary>
/// Codex's managed-CLI install recipe (AC-20) — the one place the Codex download channel is named. The host's
/// generic installer consumes this <see cref="ManagedCliDescriptor"/>; the core never knows what "codex" is.
/// </summary>
/// <remarks>
/// Route (verified against the live GitHub release <c>openai/codex@rust-v0.144.5</c>, 2026-07): releases are tagged
/// <c>rust-v&lt;version&gt;</c>; the per-target asset is <c>codex-&lt;triple&gt;.tar.gz</c> (Linux/macOS) or
/// <c>codex-&lt;triple&gt;.exe.tar.gz</c> (Windows), a single-file tarball whose entry is the binary named after the
/// triple. Codex ships only a musl Linux build (a static binary that also runs on glibc). Integrity comes from the
/// GitHub API asset <c>digest</c> (<c>sha256:…</c>) — a second channel over TLS, since the release's
/// <c>SHA256SUMS</c> asset covers only the <c>codex-package-*</c> bundles, not the bare tarball.
/// </remarks>
internal static class CodexManagedCli
{
    public const string CliName = "codex";

    private const string ReleasesApiBase = "https://api.github.com/repos/openai/codex/releases";

    public static ManagedCliDescriptor Descriptor { get; } = new()
    {
        CliName = CliName,
        ResolveLatestVersionAsync = async (http, cancellationToken) =>
        {
            // Do not trust /releases/latest to be a codex CLI release: the repo also publishes other trains, and its
            // "latest" could be one of those. List releases (newest first) and take the first published rust-v tag.
            var json = await _GetGitHubJsonAsync(http, $"{ReleasesApiBase}?per_page=30", cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException("The codex releases response was not a list.");
            }

            foreach (var release in document.RootElement.EnumerateArray())
            {
                if ((release.TryGetProperty("draft", out var draft) && draft.GetBoolean())
                    || (release.TryGetProperty("prerelease", out var prerelease) && prerelease.GetBoolean()))
                {
                    continue;
                }

                var tag = release.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() : null;
                if (!string.IsNullOrEmpty(tag) && tag.StartsWith("rust-v", StringComparison.Ordinal))
                {
                    return ParseVersion(tag);
                }
            }

            throw new InvalidOperationException("No published codex rust-v release was found.");
        },
        BuildDownloadPlanAsync = async (http, platform, version, cancellationToken) =>
        {
            var json = await _GetGitHubJsonAsync(http, $"{ReleasesApiBase}/tags/rust-v{version}", cancellationToken).ConfigureAwait(false);
            return BuildPlan(platform, json);
        },
    };

    /// <summary><c>rust-v0.144.5</c> → <c>0.144.5</c>. Internal for testing.</summary>
    internal static string ParseVersion(string tagName)
    {
        const string prefix = "rust-v";
        return tagName.StartsWith(prefix, StringComparison.Ordinal) ? tagName[prefix.Length..] : tagName;
    }

    /// <summary>The Rust target-triple for a platform (Linux is always musl). Internal for testing.</summary>
    internal static string TargetTriple(ManagedCliPlatform platform)
    {
        var arch = platform.Arch switch
        {
            "x64" => "x86_64",
            "arm64" => "aarch64",
            _ => throw new InvalidOperationException($"Codex has no build for architecture '{platform.Arch}'."),
        };

        return platform.Os switch
        {
            "linux" => $"{arch}-unknown-linux-musl",
            "darwin" => $"{arch}-apple-darwin",
            "win32" => $"{arch}-pc-windows-msvc",
            _ => throw new InvalidOperationException($"Codex has no build for OS '{platform.Os}'."),
        };
    }

    /// <summary>The release asset name for a platform — Windows adds a <c>.exe</c> before <c>.tar.gz</c>. Internal for testing.</summary>
    internal static string AssetName(ManagedCliPlatform platform)
    {
        var triple = TargetTriple(platform);
        return platform.Os == "win32" ? $"codex-{triple}.exe.tar.gz" : $"codex-{triple}.tar.gz";
    }

    /// <summary>The single tarball entry that is the binary — named after the triple (<c>.exe</c> on Windows). Internal for testing.</summary>
    internal static string EntryName(ManagedCliPlatform platform)
    {
        var triple = TargetTriple(platform);
        return platform.Os == "win32" ? $"codex-{triple}.exe" : $"codex-{triple}";
    }

    /// <summary>Builds the download plan from a target platform and a fetched GitHub release JSON. Internal (no network) for testing.</summary>
    internal static ManagedCliDownloadPlan BuildPlan(ManagedCliPlatform platform, string releaseJson)
    {
        var assetName = AssetName(platform);

        using var document = JsonDocument.Parse(releaseJson);
        if (!document.RootElement.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("The codex release JSON has no assets array.");
        }

        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
            if (!string.Equals(name, assetName, StringComparison.Ordinal))
            {
                continue;
            }

            var url = asset.TryGetProperty("browser_download_url", out var urlElement) ? urlElement.GetString() : null;
            var digest = asset.TryGetProperty("digest", out var digestElement) ? digestElement.GetString() : null;
            if (string.IsNullOrEmpty(url))
            {
                throw new InvalidOperationException($"The codex asset '{assetName}' has no download URL.");
            }

            // The URL comes from the release JSON; require it to be an https GitHub download host so a spoofed/edge
            // response cannot point the fetch at an arbitrary target. (Content is digest-bound regardless, but the
            // request target itself should not be attacker-chosen.)
            if (!_IsTrustedDownloadUrl(url))
            {
                throw new InvalidOperationException($"The codex asset '{assetName}' has an untrusted download URL ('{url}') and was refused.");
            }

            if (string.IsNullOrEmpty(digest) || !digest.StartsWith("sha256:", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"The codex asset '{assetName}' has no sha256 digest to verify against.");
            }

            var isWindows = platform.Os == "win32";
            return new ManagedCliDownloadPlan
            {
                Url = url,
                ExpectedSha256 = digest["sha256:".Length..],
                ExecutableFileName = isWindows ? "codex.exe" : "codex",
                ArchiveFormat = ManagedCliArchiveFormat.TarGz,
                ExecutableEntryName = EntryName(platform),
                NeedsExecutableBit = !isWindows,
            };
        }

        throw new InvalidOperationException($"The codex release does not contain the asset '{assetName}'.");
    }

    // An https download from GitHub's own release hosts — github.com serves the redirect, objects.githubusercontent.com the bytes.
    private static bool _IsTrustedDownloadUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && (uri.Host == "github.com"
            || uri.Host == "objects.githubusercontent.com"
            || uri.Host.EndsWith(".githubusercontent.com", StringComparison.Ordinal));

    // GitHub's API rejects requests without a User-Agent; send one and ask for the documented REST media type.
    private static async Task<string> _GetGitHubJsonAsync(HttpClient http, string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("Cockpit-ManagedCli");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }
}
