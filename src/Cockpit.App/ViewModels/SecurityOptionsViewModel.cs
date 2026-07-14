using CommunityToolkit.Mvvm.ComponentModel;
using Cockpit.Core.Abstractions.Secrets;
using Cockpit.Core.Secrets;

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
public sealed partial class SecurityOptionsViewModel(ISecretProtectionService protection) : ObservableObject
{
    [ObservableProperty]
    private bool _isEncrypted;

    [ObservableProperty]
    private bool _isMigrating;

    [ObservableProperty]
    private string _migrationCaption = string.Empty;

    [ObservableProperty]
    private double _migrationProgress;

    [ObservableProperty]
    private string? _status;

    public async Task RefreshAsync()
    {
        IsEncrypted = (await protection.GetStatusAsync().ConfigureAwait(true)).Enabled;
    }

    public async Task EnableAsync(string password)
    {
        await RunMigrationAsync(
            "Encrypting your credentials…",
            progress => protection.EnableAsync(password, progress)).ConfigureAwait(true);

        Status = "Your keys and tokens are encrypted. You will be asked for this password the next time the cockpit starts.";
    }

    public async Task DisableAsync()
    {
        await RunMigrationAsync(
            "Writing your credentials back in the clear…",
            progress => protection.DisableAsync(progress)).ConfigureAwait(true);

        Status = "Encryption is off. Your keys and tokens are readable in cockpit.json again, and the cockpit starts without asking for a password.";
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
