using System.Text.Json;
using Cockpit.Plugins.Abstractions.ManagedCli;

namespace Cockpit.Plugin.ClaudeProvider;

// Claude's managed-CLI install recipe (AC-20) — the one place the Claude download channel is named. The host's
// generic installer consumes this `ManagedCliDescriptor`; the core never knows what "claude" is.
// Route (verified against Anthropic's own install.sh, 2026-07): the latest version is a plain string at
// `.../claude-code-releases/latest`; each version has a `manifest.json` keyed by platform
// (`linux-x64`, `darwin-arm64`, `win32-x64`, …, plus `-musl` variants) with the binary's name
// and its SHA-256; the binary itself is `.../&lt;version&gt;/&lt;platform&gt;/&lt;binary&gt;`. The endpoint is
// not a published API contract but is the de-facto production channel Anthropic's own installer depends on.
internal static class ClaudeManagedCli
{
    public const string CliName = "claude";

    private const string ReleasesBase = "https://downloads.claude.ai/claude-code-releases";

    public static ManagedCliDescriptor Descriptor { get; } = new()
    {
        CliName = CliName,
        ResolveLatestVersionAsync = async (http, cancellationToken) =>
            (await http.GetStringAsync($"{ReleasesBase}/latest", cancellationToken).ConfigureAwait(false)).Trim(),
        BuildDownloadPlanAsync = async (http, platform, version, cancellationToken) =>
        {
            var manifestJson = await http
                .GetStringAsync($"{ReleasesBase}/{version}/manifest.json", cancellationToken)
                .ConfigureAwait(false);
            return BuildPlan(version, platform, manifestJson);
        },
    };

    // The manifest platform key for a target — `&lt;os&gt;-&lt;arch&gt;`, with a `-musl` suffix on musl Linux. Internal for testing.
    internal static string PlatformKey(ManagedCliPlatform platform) =>
        $"{platform.Os}-{platform.Arch}" + (platform is { Os: "linux", IsMusl: true } ? "-musl" : string.Empty);

    // Builds the download plan from a resolved version, a target platform and the fetched manifest JSON. Internal (no network) for testing.
    internal static ManagedCliDownloadPlan BuildPlan(string version, ManagedCliPlatform platform, string manifestJson)
    {
        var key = PlatformKey(platform);

        using var document = JsonDocument.Parse(manifestJson);
        if (!document.RootElement.TryGetProperty("platforms", out var platforms)
            || !platforms.TryGetProperty(key, out var entry))
        {
            throw new InvalidOperationException($"The claude release manifest has no build for platform '{key}'.");
        }

        var binary = entry.TryGetProperty("binary", out var binaryElement) ? binaryElement.GetString() : null;
        var checksum = entry.TryGetProperty("checksum", out var checksumElement) ? checksumElement.GetString() : null;
        if (string.IsNullOrEmpty(binary) || string.IsNullOrEmpty(checksum))
        {
            throw new InvalidOperationException($"The claude manifest entry for '{key}' is missing its binary name or checksum.");
        }

        var isWindows = platform.Os == "win32";
        return new ManagedCliDownloadPlan
        {
            Url = $"{ReleasesBase}/{version}/{key}/{binary}",
            ExpectedSha256 = checksum,
            ExecutableFileName = isWindows ? "claude.exe" : "claude",
            ArchiveFormat = ManagedCliArchiveFormat.RawBinary,
            NeedsExecutableBit = !isWindows,
        };
    }
}
