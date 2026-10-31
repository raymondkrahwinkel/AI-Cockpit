using Avalonia.Controls;
using Cockpit.App.ViewModels;

namespace Cockpit.App.Views;

public partial class ServerAssistantPage : UserControl
{
    public ServerAssistantPage()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ServerAssistantViewModel page)
            {
                page.ShowProfileDialog = _ShowProfileDialogAsync;
            }
        };
    }

    // AC-1995: the Assistant Profile dialog over the server's slot, owned by the window this page sits in.
    private async Task _ShowProfileDialogAsync(AssistantProfileDialogViewModel dialog)
    {
        if (TopLevel.GetTopLevel(this) is Window owner)
        {
            await new AssistantProfileDialog { DataContext = dialog }.ShowDialog(owner);
        }
    }
}
