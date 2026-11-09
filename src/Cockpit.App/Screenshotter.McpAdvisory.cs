using Avalonia.Controls;
using Cockpit.App.Controls;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Mcp;

namespace Cockpit.App;

// AC-2219: a plugin's advice about the Depot gateway in the three places that show it.
internal static partial class Screenshotter
{
    private const string GatewayServer = "Depot gateway: Voorbeeld BV";

    private static void _StageMcpAdvisory() =>
        McpServerAdvisories.Shared.Set(GatewayServer, new McpServerAdvisory(
            McpAdvisoryLevel.Warning, "52 tools, 31.4 KB of tool descriptions. Agents get slower and less precise with this many.",
            "https://depot.example/org/voorbeeld/mcp/performance"));

    private static Window _McpAdvisoryChecklist()
    {
        _StageMcpAdvisory();
        var servers = new System.Collections.ObjectModel.ObservableCollection<McpServerSelectionItemViewModel>
        {
            new("youtrack"), new(GatewayServer), new("playwright"),
        };
        return new Window
        {
            Width = 560,
            Height = 260,
            Content = new McpServerChecklist { Servers = servers, IsExpanded = true, Margin = new Avalonia.Thickness(16) },
        };
    }

    private static CockpitViewModel _McpAdvisoryDetails()
    {
        _StageMcpAdvisory();
        var cockpit = _SimpleStandConversation(withCost: true);
        ((SessionPanelViewModel)_Named(cockpit, "kind→staging")).McpServerSelection = new HashSet<string> { "youtrack", GatewayServer };
        return cockpit;
    }

    private static OptionsDialog _McpAdvisoryOptions()
    {
        _StageMcpAdvisory();
        var dialog = _OptionsOnTab("mcp-servers");
        var mcpServers = new McpServersViewModel();
        mcpServers.LoadAsync().GetAwaiter().GetResult();
        dialog.DataContext = new CockpitViewModel { McpServers = mcpServers };
        return dialog;
    }
}
