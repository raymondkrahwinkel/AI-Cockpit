namespace Cockpit.Plugins.Abstractions.UI.Onboarding;

// AC-1509: what a first-start route may ask of the host while the wizard is open (IFirstStartPluginUi).
public interface IFirstStartHost
{
    /// <summary>
    /// The plugin's own channel, to reach the handlers its backend part registered in
    /// <see cref="IFirstStartPlugin.InitializeFirstStart"/>.
    /// </summary>
    IPluginUiChannel Channel { get; }

    /// <summary>
    /// Restores the backup <paramref name="backupId"/> from the registered backup target <paramref name="targetId"/> the
    /// way the wizard's own restore does: the settings and the plugins come back, the wizard is done and the cockpit
    /// restarts. Returns the sentence to show — what went wrong, or that it is restarting.
    /// </summary>
    Task<string> RestoreBackupAsync(string targetId, string backupId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads a provisioning manifest (manifest-v1) and, when it is valid, hands what it would set up to
    /// <paramref name="consent"/> as lines to show. Applies it only when that answers true, and returns the report's
    /// lines; a refused manifest returns its reason and applies nothing.
    /// </summary>
    Task<IReadOnlyList<string>> ApplyManifestAsync(
        string manifestJson,
        Func<IReadOnlyList<string>, Task<bool>> consent,
        CancellationToken cancellationToken = default);
}
