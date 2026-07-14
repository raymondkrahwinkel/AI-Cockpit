using CommunityToolkit.Mvvm.ComponentModel;
using Cockpit.Core.Abstractions.Secrets;

namespace Cockpit.App.ViewModels;

/// <summary>
/// Backs the window that stands in front of the cockpit when the operator encrypted their credentials: the
/// password is the key, so nothing that reads a setting can run until it has been typed.
/// </summary>
public sealed partial class UnlockViewModel(ISecretProtectionService protection) : ObservableObject
{
    /// <summary>Design-time constructor for the previewer.</summary>
    public UnlockViewModel()
        : this(new UnprotectedSecrets())
    {
    }

    [ObservableProperty]
    private string _password = string.Empty;

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>Raised once the password was right and the settings can be read. The app starts from here.</summary>
    public event EventHandler? Unlocked;

    /// <summary>The operator asked for the way out of a forgotten password. The window confirms before doing it.</summary>
    public event EventHandler? ResetRequested;

    public async Task UnlockAsync()
    {
        if (IsBusy || Password.Length == 0)
        {
            return;
        }

        IsBusy = true;
        Error = null;
        try
        {
            // Deriving the key is deliberately slow (210k PBKDF2 iterations — that slowness is the point), so it
            // does not run on the UI thread: a window frozen mid-keystroke reads as a crash, not as work.
            if (await Task.Run(() => protection.UnlockAsync(Password)).ConfigureAwait(true))
            {
                Unlocked?.Invoke(this, EventArgs.Empty);

                return;
            }

            // Which of the two it was — a typo or a damaged file — is something AES-GCM cannot tell us, so the
            // message does not pretend to know.
            Error = "That password does not open this configuration.";
            Password = string.Empty;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void RequestReset() => ResetRequested?.Invoke(this, EventArgs.Empty);

    public async Task ResetAsync()
    {
        IsBusy = true;
        try
        {
            await protection.ResetForgottenPasswordAsync().ConfigureAwait(true);
            Unlocked?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            IsBusy = false;
        }
    }

}
