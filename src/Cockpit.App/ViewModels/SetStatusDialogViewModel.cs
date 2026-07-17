using CommunityToolkit.Mvvm.ComponentModel;

namespace Cockpit.App.ViewModels;

/// <summary>
/// Backs the Set-status dialog (AC-32): a free-text line the operator sets by hand for a session, seeded with
/// its current <see cref="SessionPanelViewModel.Statusline"/>. The same value the agent sets through the
/// <c>cockpit-session</c> MCP, edited here instead — the dialog writes it back to that one property.
/// </summary>
public sealed partial class SetStatusDialogViewModel : ObservableObject
{
    /// <summary>Design-time constructor for the previewer.</summary>
    public SetStatusDialogViewModel()
        : this("AC-13 — wiring the status line")
    {
    }

    public SetStatusDialogViewModel(string currentStatusline)
    {
        StatusText = currentStatusline;
    }

    [ObservableProperty]
    private string _statusText = string.Empty;
}
