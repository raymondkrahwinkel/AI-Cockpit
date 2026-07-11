using CommunityToolkit.Mvvm.ComponentModel;

namespace Cockpit.App.ViewModels;

/// <summary>
/// One checkbox row in the New-session dialog's MCP-server checklist (#44): a registry server's name plus
/// whether it should be loaded for the session about to start. Defaults to checked, matching the pre-#44
/// behaviour of loading every enabled registry server.
/// </summary>
public partial class McpServerSelectionItemViewModel : ViewModelBase
{
    public string Name { get; }

    [ObservableProperty]
    private bool _isEnabledForSession = true;

    public McpServerSelectionItemViewModel(string name)
    {
        Name = name;
    }
}
