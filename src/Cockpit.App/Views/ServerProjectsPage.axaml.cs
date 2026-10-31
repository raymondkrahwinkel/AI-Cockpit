using Avalonia.Controls;
using Cockpit.App.ViewModels;

namespace Cockpit.App.Views;

public partial class ServerProjectsPage : UserControl
{
    public ServerProjectsPage()
    {
        InitializeComponent();
        DetachedFromVisualTree += (_, _) =>
        {
            if (DataContext is ServerAdminViewModel admin)
            {
                admin.CloseGitHubSignInCommand.Execute(null);
            }
        };
    }
}
