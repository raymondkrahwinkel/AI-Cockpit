using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Workflows;

// The Workflows settings view (opened from the plugin manager's gear): a single toggle for whether the plugin's MCP
// server is offered to sessions (AC-40). Implements `IPluginSettingsView` so the host dialog shows a
// Save button.
internal sealed class WorkflowsSettingsControl : UserControl, IPluginSettingsView
{
    private readonly WorkflowsSettings _settings;
    private readonly CheckBox _mcpEnabled;

    public WorkflowsSettingsControl(ICockpitHost host, WorkflowsSettings settings)
    {
        _settings = settings;

        _mcpEnabled = new CheckBox
        {
            Content = "Let sessions use the workflows MCP",
            IsChecked = settings.McpEnabled,
        };

        var description = new TextBlock
        {
            Text = "Offers the cockpit-workflows tools (list, read, run and create/edit flows) to your sessions. "
                + "Turn it off to keep an agent from reaching your workflows.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11,
            Opacity = 0.7,
        };

        // AC-1043: the SDK-drawn "?" beside the MCP toggle, pointing at this plugin's own Docs page section
        // on what a workflow step's MCP surface can (and cannot) do without the operator's say-so.
        var mcpRow = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Children = { _mcpEnabled, host.CreateHelpHint("how-it-works", "consent-tiers") },
        };

        Content = new StackPanel
        {
            Spacing = 8,
            Margin = new Thickness(4),
            Children = { mcpRow, description },
        };
    }

    // AC-1004, criterion 3: the old `Save()` was this one storage write and nothing else — the MCP toggle is read
    // fresh whenever the plugin is asked for its servers, so there is nothing to re-register here.
    public bool TryStage(out Action? commit, out string? error)
    {
        commit = () => _settings.SaveMcpEnabled(_mcpEnabled.IsChecked ?? true);
        error = null;
        return true;
    }
}
