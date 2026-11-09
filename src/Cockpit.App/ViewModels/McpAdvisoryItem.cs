using Cockpit.App.Services;
using Cockpit.Core.Mcp;
using CommunityToolkit.Mvvm.Input;

namespace Cockpit.App.ViewModels;

// AC-2219: a plugin's line about one MCP server, as the three places that show it read it.
public sealed partial class McpAdvisoryItem(string name, McpServerAdvisory advisory)
{
    public string Name => name;

    public string Text => advisory.Text;

    public bool IsCritical => advisory.Level == McpAdvisoryLevel.Critical;

    public bool HasLink => ExternalLink.TryParseWebAddress(advisory.Link, out _);

    // The server's name leads the line where several servers share a list.
    public string Line => $"{name}: {advisory.Text}";

    public static McpAdvisoryItem? For(string name) =>
        McpServerAdvisories.Shared.Get(name) is { } advisory ? new McpAdvisoryItem(name, advisory) : null;

    [RelayCommand]
    private void OpenLink() => ExternalLink.TryOpen(advisory.Link);
}
