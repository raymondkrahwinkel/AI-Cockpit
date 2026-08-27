using CommunityToolkit.Mvvm.ComponentModel;

namespace Cockpit.App.ViewModels;

// A half-filled row is dropped on save rather than held against the operator; `EditableMcpServerViewModel.ToConfig` is
// what applies that filter, via `McpHeader.IsComplete` (AC-354).
public partial class McpHeaderRowViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private string _value;

    public McpHeaderRowViewModel(string name = "", string value = "")
    {
        _name = name;
        _value = value;
    }
}
