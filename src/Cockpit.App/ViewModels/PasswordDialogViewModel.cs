using CommunityToolkit.Mvvm.ComponentModel;

namespace Cockpit.App.ViewModels;

/// <summary>
/// Asks for a password: the new one twice (typing it wrong once and never finding out is how an operator locks
/// themselves out), and the current one as well when the password is being changed.
/// </summary>
public sealed partial class PasswordDialogViewModel : ObservableObject
{
    /// <summary>Design-time constructor for the previewer.</summary>
    public PasswordDialogViewModel()
        : this("Encrypt your credentials", "Pick a password.", requiresCurrent: false)
    {
    }

    public PasswordDialogViewModel(string title, string message, bool requiresCurrent)
    {
        Title = title;
        Message = message;
        RequiresCurrent = requiresCurrent;
    }

    public string Title { get; }

    public string Message { get; }

    public bool RequiresCurrent { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    private string _currentPassword = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    [NotifyPropertyChangedFor(nameof(Mismatch))]
    private string _newPassword = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    [NotifyPropertyChangedFor(nameof(Mismatch))]
    private string _confirmation = string.Empty;

    public bool Mismatch => Confirmation.Length > 0 && NewPassword != Confirmation;

    public bool CanConfirm =>
        NewPassword.Length > 0
        && NewPassword == Confirmation
        && (!RequiresCurrent || CurrentPassword.Length > 0);
}
