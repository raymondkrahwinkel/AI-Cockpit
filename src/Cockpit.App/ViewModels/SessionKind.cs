namespace Cockpit.App.ViewModels;

/// <summary>Which kind of session the New-session dialog is configuring: an SDK chat panel or a TTY terminal panel.</summary>
public enum SessionKind
{
    Sdk,
    Tty,
}
