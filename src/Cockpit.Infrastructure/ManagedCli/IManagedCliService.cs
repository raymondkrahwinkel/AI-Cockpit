using Cockpit.Plugins.Abstractions.ManagedCli;

namespace Cockpit.Infrastructure.ManagedCli;

/// <summary>
/// The host-side managed-CLI facility (AC-20): a generic installer that downloads, verifies, unpacks and resolves a
/// provider's CLI from a plugin-supplied <see cref="ManagedCliDescriptor"/>. Names no provider — Claude, Codex and
/// anything later are served by the same code, differing only in the descriptor they register.
/// </summary>
public interface IManagedCliService
{
    /// <summary>Records a plugin's install recipe under its <see cref="ManagedCliDescriptor.CliName"/>. Idempotent — re-registering the same name replaces it.</summary>
    void Register(ManagedCliDescriptor descriptor);

    /// <summary>
    /// The path to the newest installed version of <paramref name="cliName"/> on disk, or <see langword="null"/> when
    /// none is installed. A pure filesystem lookup — it does not download; that keeps it safe to call on the hot path
    /// of resolving an executable at session spawn, where "not installed" must mean "fall back to PATH", never a stall.
    /// </summary>
    string? ResolveInstalledPath(string cliName);

    /// <summary>
    /// Ensures the latest version of <paramref name="cliName"/> is installed and returns where it landed. Resolves the
    /// latest version through the descriptor, and if that version is not already on disk downloads it, verifies its
    /// checksum, unpacks it and places it atomically under <c>&lt;StateRoot&gt;/cli/&lt;name&gt;/&lt;version&gt;/</c>.
    /// A checksum mismatch, a missing descriptor or a network failure is returned as an unsuccessful result rather than
    /// thrown — the caller (a config view, an update check) reports it; the app is never taken down by a managed CLI.
    /// </summary>
    Task<ManagedCliInstallResult> EnsureInstalledAsync(string cliName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes every installed version of <paramref name="cliName"/> (AC-20 "uitzetbaar"): the managed copy is a
    /// convenience, so deleting it simply lets resolution fall back to a pin or PATH. Returns whether anything was
    /// removed. Never throws — a locked file is reported as "not fully removed" rather than crashing a settings action.
    /// </summary>
    bool RemoveInstalled(string cliName);
}
