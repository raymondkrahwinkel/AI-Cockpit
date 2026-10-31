using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.App.Logging;
using Cockpit.Core.Abstractions.Secrets;

namespace Cockpit.App.ViewModels;

// Backs the window that stands in front of the cockpit when the operator encrypted their credentials: the
// password is the key, so nothing that reads a setting can run until it has been typed.
public sealed partial class UnlockViewModel(ISecretProtectionService protection) : ObservableObject
{
    // Design-time constructor for the previewer.
    public UnlockViewModel()
        : this(new UnprotectedSecrets())
    {
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UnlockIsPrimary))]
    private string _password = string.Empty;

    // AC-1879: Windows Hello as a second way through the same step; set by the window that owns the prompt.
    public ISecretKeySigner? HelloSigner { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UnlockIsPrimary))]
    private bool _offersHello;

    // After a cancelled prompt the button reads "Try … again" and the password row takes the lead.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UnlockIsPrimary), nameof(HelloButtonText))]
    private bool _helloTried;

    [ObservableProperty]
    private bool _isWaitingForHello;

    // Amber, not red: nothing was wrong with the configuration (a wrong password stays red in `Error`).
    [ObservableProperty]
    private string? _notice;

    public string HelloButtonText => HelloTried ? "Try Windows Hello again" : "Unlock with Windows Hello";

    // The Unlock button is the accent one unless Hello is offered first and nothing has been typed yet.
    public bool UnlockIsPrimary => !OffersHello || HelloTried || Password.Length > 0;

    // Raised when Hello did not unlock, so the window puts the cursor in the password field.
    public event EventHandler? PasswordFocusRequested;

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    private bool _isBusy;

    // AC-1487: a `--remote` window unlocks the local registry but never resets it; that stays with the local Cockpit.
    public bool OffersReset { get; init; } = true;

    // Raised once the password was right and the settings can be read. The app starts from here.
    public event EventHandler? Unlocked;

    // The operator asked for the way out of a forgotten password. The window confirms before doing it.
    public event EventHandler? ResetRequested;

    // The Hello prompt in flight; typing the password and unlocking with it closes the prompt (AC-1879).
    private CancellationTokenSource? _hello;

    // Hello and the password can race; the app starts once.
    private bool _raisedUnlocked;

    private void _RaiseUnlocked()
    {
        if (!_raisedUnlocked)
        {
            _raisedUnlocked = true;
            Unlocked?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task UnlockAsync()
    {
        if (IsBusy || Password.Length == 0)
        {
            return;
        }

        _hello?.Cancel();
        IsBusy = true;
        Error = null;
        Notice = null;
        try
        {
            // Deriving the key is deliberately slow (210k PBKDF2 iterations — that slowness is the point), so it
            // does not run on the UI thread: a window frozen mid-keystroke reads as a crash, not as work.
            if (await Task.Run(() => protection.UnlockAsync(Password)).ConfigureAwait(true))
            {
                _RaiseUnlocked();

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

    // Shows the Hello button when this configuration has Hello on and this machine can do it; true means: prompt now.
    public async Task<bool> OfferHelloAsync()
    {
        OffersHello = HelloSigner is { } signer
            && (await protection.GetStatusAsync().ConfigureAwait(true)).HelloEnabled
            && await signer.IsAvailableAsync().ConfigureAwait(true);
        return OffersHello;
    }

    public async Task UnlockWithHelloAsync()
    {
        if (IsBusy || IsWaitingForHello || HelloSigner is not { } signer || !OffersHello)
        {
            return;
        }

        // Not IsBusy: the password row stays usable while the prompt is up, so a prompt that never answers strands nobody.
        using var hello = _hello = new CancellationTokenSource();
        IsWaitingForHello = true;
        Error = null;
        Notice = null;
        try
        {
            switch (await protection.UnlockWithHelloAsync(signer, hello.Token).ConfigureAwait(true))
            {
                case HelloUnlockResult.Unlocked:
                    LifecycleLog.Write("Unlocked with Windows Hello; no password key derivation ran.");
                    _RaiseUnlocked();
                    return;
                case HelloUnlockResult.KeyGone:
                    OffersHello = false;
                    Notice = "Windows Hello can no longer open this configuration on this computer. Use your password; you can turn Windows Hello on again in Settings.";
                    break;
                case HelloUnlockResult.NotOffered:
                    OffersHello = false;
                    break;
                default:
                    HelloTried = true;
                    Notice = "Windows Hello did not unlock. Type your password, or try Windows Hello again.";
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            // The password got there first.
            return;
        }
        finally
        {
            _hello = null;
            IsWaitingForHello = false;
        }

        PasswordFocusRequested?.Invoke(this, EventArgs.Empty);
    }

    // The Hello button. A failure leaves the password row, which is all the operator needs.
    [RelayCommand]
    private async Task TryHelloAsync()
    {
        try
        {
            await UnlockWithHelloAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            LifecycleLog.Write($"Unlock with Windows Hello failed: {exception}");
            Error = "Could not unlock. Try again, or type your password.";
        }
    }

    // The screen-lock window offers the button once it is open, without prompting by itself.
    [RelayCommand]
    private async Task ShowHelloAsync()
    {
        try
        {
            await OfferHelloAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            LifecycleLog.Write($"Offering Windows Hello failed: {exception}");
        }
    }

    public void RequestReset() => ResetRequested?.Invoke(this, EventArgs.Empty);

    public async Task ResetAsync()
    {
        IsBusy = true;
        try
        {
            // The Hello key wraps the key being forgotten, so it goes too (AC-1879).
            if (HelloSigner is { } signer)
            {
                await protection.DisableHelloAsync(signer).ConfigureAwait(true);
            }

            await protection.ResetForgottenPasswordAsync().ConfigureAwait(true);
            _RaiseUnlocked();
        }
        finally
        {
            IsBusy = false;
        }
    }

}
