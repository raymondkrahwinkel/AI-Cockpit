using CommunityToolkit.Mvvm.ComponentModel;

namespace Cockpit.App.ViewModels;

// Backs the Set-status dialog (AC-32): a free-text line the operator sets by hand for a session, seeded with
// its current `SessionPanelViewModel.Statusline`. The same value the agent sets through the
// `cockpit-session` MCP, edited here instead — the dialog writes it back to that one property.
public sealed partial class SetStatusDialogViewModel : ObservableObject
{
    // Design-time constructor for the previewer.
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
