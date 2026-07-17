namespace Cockpit.Plugins.Abstractions.ManagedCli;

/// <summary>
/// The outcome of a managed-CLI install (AC-20): the installed executable's path on success, or a human-readable
/// reason on failure. A failure is a value here, not an exception — installing a CLI is a convenience that must never
/// crash the cockpit. In the plugin SDK so <see cref="ICockpitHost.InstallManagedCliAsync"/> can return it.
/// </summary>
/// <param name="Success">Whether a usable executable is now on disk.</param>
/// <param name="Version">The version that was installed or already present, when known.</param>
/// <param name="ExecutablePath">The path to the executable on success; <see langword="null"/> on failure.</param>
/// <param name="Error">A message the caller can surface (a toast, a config-view line) on failure; <see langword="null"/> on success.</param>
public sealed record ManagedCliInstallResult(bool Success, string? Version, string? ExecutablePath, string? Error)
{
    public static ManagedCliInstallResult Ok(string version, string executablePath) => new(true, version, executablePath, null);

    public static ManagedCliInstallResult Fail(string error) => new(false, null, null, error);
}
