using CommunityToolkit.Mvvm.ComponentModel;

namespace Cockpit.App.ViewModels;

// One editable row of an HTTP MCP server's custom headers (AC-354): a header name and its value. The value
// field always masks in the dialog, the same as the API-key field beside it — a custom header is in practice
// always a credential (see `Cockpit.Core.Mcp.McpHeader`). A half-filled row is dropped on save
// rather than held against the operator; `EditableMcpServerViewModel.ToConfig` is what applies
// that filter, via `McpHeader.IsComplete`.
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
