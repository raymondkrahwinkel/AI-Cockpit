using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.Core.Abstractions.Secrets;
using Cockpit.Core.Abstractions.Terminal;
using Cockpit.Core.Secrets;
using Cockpit.Core.Terminal;

namespace Cockpit.App.ViewModels;

/// <summary>
/// The Security tab: whether the credentials in <c>cockpit.json</c> are encrypted, and the migration that runs
/// when the operator changes their mind either way.
/// <para>
/// Both directions migrate, and both are shown while they happen. The work is usually over in a blink, but this
/// is the one operation that rewrites every credential the operator has: a screen that flickers is better than
/// an app that goes quiet while it does that.
/// </para>
/// </summary>
public sealed partial class SecurityOptionsViewModel(
    ISecretProtectionService protection,
    ITerminalAccessSwitch? terminalAccessSwitch = null,
    ITerminalAccessSettingsStore? terminalAccessSettings = null) : ObservableObject
{
    // True only while RefreshAsync seeds the toggle from disk, so setting the property then does not turn around and
    // write the same value straight back.
    private bool _loadingTerminalAccess;

    [ObservableProperty]
    private bool _isEncrypted;

    /// <summary>
    /// The terminal-access master switch (AC-34): off by default, an opt-in. While off, the <c>cockpit-terminal</c>
    /// MCP is not advertised to any session — for an agent the feature does not exist. Turning it on makes it
    /// reachable, still behind a per-pane Approve/Deny. Persisted, and flipped live so the next session sees the
    /// change without a restart.
    /// </summary>
    [ObservableProperty]
    private bool _terminalAccessEnabled;

    [ObservableProperty]
    private bool _isMigrating;

    [ObservableProperty]
    private string _migrationCaption = string.Empty;

    [ObservableProperty]
    private double _migrationProgress;

    [ObservableProperty]
    private string? _status;

    /// <summary>
    /// Whether the app-level awareness banner (AC-41) should show: encryption is off and the settings hold at
    /// least one credential in the clear that the operator has not dismissed the warning for. Bound by
    /// <c>CockpitView.axaml</c>'s banner, and re-read on every <see cref="RefreshAsync"/> — startup, a save that
    /// wrote a new credential, and after either migration — so a single property is the whole of its visibility.
    /// </summary>
    [ObservableProperty]
    private bool _showUnprotectedBanner;

    public async Task RefreshAsync()
    {
        var status = await protection.GetStatusAsync().ConfigureAwait(true);
        IsEncrypted = status.Enabled;
        ShowUnprotectedBanner = status.ShouldWarnUnprotected;

        // Absent in the design-time/unit-test graph — the toggle then stays off and inert.
        if (terminalAccessSettings is null)
        {
            return;
        }

        var terminal = await terminalAccessSettings.LoadAsync().ConfigureAwait(true);
        _loadingTerminalAccess = true;
        TerminalAccessEnabled = terminal.Enabled;
        _loadingTerminalAccess = false;
        if (terminalAccessSwitch is not null)
        {
            terminalAccessSwitch.Enabled = terminal.Enabled;
        }
    }

    // The toggle changed. Flip the live switch at once (so the next session sees it without a restart) and persist,
    // unless we are only seeding the value from disk in RefreshAsync (or the store is absent in a test graph).
    async partial void OnTerminalAccessEnabledChanged(bool value)
    {
        if (_loadingTerminalAccess || terminalAccessSettings is null)
        {
            return;
        }

        if (terminalAccessSwitch is not null)
        {
            terminalAccessSwitch.Enabled = value;
        }

        await terminalAccessSettings.SaveAsync(new TerminalAccessSettings { Enabled = value }).ConfigureAwait(true);
    }

    /// <summary>
    /// Dismisses the awareness banner for the credentials now in the file (AC-41). Hides it at once, then persists
    /// the dismissal so it stays hidden across restarts — until a new credential changes the set and brings it back.
    /// </summary>
    [RelayCommand]
    private async Task DismissBannerAsync()
    {
        ShowUnprotectedBanner = false;
        await protection.DismissUnprotectedWarningAsync().ConfigureAwait(true);
    }

    public async Task EnableAsync(string password)
    {
        await RunMigrationAsync(
            "Encrypting your credentials…",
            progress => protection.EnableAsync(password, progress)).ConfigureAwait(true);

        Status = "Your keys and tokens are encrypted. You will be asked for this password the next time AI-Cockpit starts.";
    }

    public async Task DisableAsync()
    {
        await RunMigrationAsync(
            "Writing your credentials back in the clear…",
            progress => protection.DisableAsync(progress)).ConfigureAwait(true);

        Status = "Encryption is off. Your keys and tokens are readable in cockpit.json again, and AI-Cockpit starts without asking for a password.";
    }

    public async Task ChangePasswordAsync(string currentPassword, string newPassword)
    {
        try
        {
            await RunMigrationAsync(
                "Re-encrypting your credentials…",
                progress => protection.ChangePasswordAsync(currentPassword, newPassword, progress)).ConfigureAwait(true);

            Status = "Your password has been changed.";
        }
        catch (SecretProtectionException)
        {
            Status = "That is not your current password — nothing was changed.";
        }
    }

    private async Task RunMigrationAsync(string caption, Func<IProgress<SecretMigrationProgress>, Task> migrate)
    {
        IsMigrating = true;
        MigrationCaption = caption;
        MigrationProgress = 0;
        Status = null;

        try
        {
            var progress = new Progress<SecretMigrationProgress>(report =>
                MigrationProgress = report.Total == 0 ? 100 : 100.0 * report.Completed / report.Total);

            // Off the UI thread: deriving the key is deliberately expensive, and a window that stops repainting
            // in the middle of rewriting the operator's credentials reads as a crash.
            await Task.Run(() => migrate(progress)).ConfigureAwait(true);

            MigrationProgress = 100;
            await RefreshAsync().ConfigureAwait(true);
        }
        finally
        {
            IsMigrating = false;
        }
    }
}
