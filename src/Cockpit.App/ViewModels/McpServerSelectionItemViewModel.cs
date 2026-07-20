using CommunityToolkit.Mvvm.ComponentModel;

namespace Cockpit.App.ViewModels;

/// <summary>
/// One checkbox row in an MCP-server checklist: a server's name plus whether it is ticked. Used both in the
/// New-session dialog for the per-session selection (#44) and in the profile editor for a profile's saved
/// pre-selection (AC-130). Defaults to checked, matching the pre-#44 behaviour of loading every enabled server.
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
