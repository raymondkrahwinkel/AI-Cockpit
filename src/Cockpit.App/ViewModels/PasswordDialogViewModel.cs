using CommunityToolkit.Mvvm.ComponentModel;

namespace Cockpit.App.ViewModels;

// Asks for a password: the new one twice (typing it wrong once and never finding out is how an operator locks
// themselves out), and the current one as well when the password is being changed.
public sealed partial class PasswordDialogViewModel : ObservableObject
{
    // Design-time constructor for the previewer.
    public PasswordDialogViewModel()
        : this("Encrypt your credentials", "Pick a password.", requiresCurrent: false)
    {
    }

    public PasswordDialogViewModel(string title, string message, bool requiresCurrent, string? confirmOnlyNote = null)
    {
        Title = title;
        Message = message;
        RequiresCurrent = requiresCurrent;
        ConfirmOnlyNote = confirmOnlyNote;
    }

    // AC-1879: set when the existing password is only being confirmed (turning on Windows Hello): one field, no
    // "again" box and no no-recovery warning, since nothing new is being chosen. The note stands under the field.
    public string? ConfirmOnlyNote { get; }

    public bool ConfirmOnly => ConfirmOnlyNote is not null;

    public bool ChoosesPassword => !ConfirmOnly;

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
        && (ConfirmOnly || NewPassword == Confirmation)
        && (!RequiresCurrent || CurrentPassword.Length > 0);
}
