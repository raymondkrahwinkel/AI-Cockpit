namespace Cockpit.Plugins.Abstractions.ManagedCli;

/// <summary>
/// A provider plugin's recipe for installing and keeping its CLI up to date (AC-20) — the one place any
/// provider-specific knowledge lives. The host's managed-CLI installer is generic (download → verify → unpack →
/// place → resolve) and names no provider; a plugin contributes this descriptor via
/// <see cref="ICockpitHost.AddManagedCli"/>, and the same installer serves Claude, Codex and anything later without
/// change. An init-only record so a later field is a binary-safe SDK addition.
/// </summary>
/// <remarks>
/// Nothing here logs the user in: a managed install delivers only the binary. Auth stays the user's own, exactly as
/// when the CLI is on PATH. And nothing here is mandatory to run the CLI — a profile can still pin an absolute path
/// or fall back to PATH; the managed copy is a convenience the resolver prefers, not a dependency.
/// </remarks>
public sealed record ManagedCliDescriptor
{
    /// <summary>The CLI's name — the key the host stores it under (<c>&lt;StateRoot&gt;/cli/&lt;CliName&gt;/…</c>) and resolves it by. E.g. <c>claude</c>, <c>codex</c>.</summary>
    public required string CliName { get; init; }

    /// <summary>
    /// Discovers the latest available version string from the provider's own channel (Claude:
    /// <c>downloads.claude.ai/…/latest</c>; Codex: the GitHub releases API). Given the shared
    /// <see cref="System.Net.Http.HttpClient"/> so a plugin does not manage its own socket lifetime.
    /// </summary>
    public required Func<HttpClient, CancellationToken, Task<string>> ResolveLatestVersionAsync { get; init; }

    /// <summary>
    /// Builds the concrete <see cref="ManagedCliDownloadPlan"/> for a resolved version on the running machine's
    /// <see cref="ManagedCliPlatform"/> — mapping the generic platform triple to the channel's own key/target-triple,
    /// fetching whatever manifest is needed to find the URL and its checksum. Given the shared
    /// <see cref="System.Net.Http.HttpClient"/>, the target <see cref="ManagedCliPlatform"/>, and the version string.
    /// </summary>
    public required Func<HttpClient, ManagedCliPlatform, string, CancellationToken, Task<ManagedCliDownloadPlan>> BuildDownloadPlanAsync { get; init; }
}
