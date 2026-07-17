namespace Cockpit.Plugins.Abstractions.ManagedCli;

/// <summary>
/// The installed and latest-available versions of a managed CLI (AC-20), so a config view can tell whether an update
/// is actually available rather than offering an "Update" that may do nothing. Either value may be
/// <see langword="null"/>: <see cref="InstalledVersion"/> when nothing is installed, <see cref="LatestVersion"/> when
/// the provider's channel could not be reached (offline) — in which case the caller should fall back rather than claim
/// "up to date".
/// </summary>
/// <param name="InstalledVersion">The newest version on disk, or <see langword="null"/> when none is installed.</param>
/// <param name="LatestVersion">The latest version the provider offers, or <see langword="null"/> when it could not be determined.</param>
public sealed record ManagedCliStatus(string? InstalledVersion, string? LatestVersion);
