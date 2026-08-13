namespace Cockpit.Infrastructure.ManagedCli;

/// <summary>
/// The per-managed-CLI auto-update switch (AC-767) — whether the background update check installs a newer version
/// itself instead of only toasting that one exists. Default on for every CLI; turning one off is what writes.
/// </summary>
public interface IManagedCliAutoUpdateStore
{
    Task<bool> IsEnabledAsync(string cliName, CancellationToken cancellationToken = default);

    Task SetAsync(string cliName, bool enabled, CancellationToken cancellationToken = default);
}
