using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Workflows;

/// <summary>
/// The Workflows settings view (opened from the plugin manager's gear): a single toggle for whether the plugin's MCP
/// server is offered to sessions (AC-40). Implements <see cref="IPluginSettingsView"/> so the host dialog shows a
/// Save button.
/// </summary>
internal sealed class WorkflowsSettingsControl : UserControl, IPluginSettingsView
{
    private readonly WorkflowsSettings _settings;
    private readonly CheckBox _mcpEnabled;

    public WorkflowsSettingsControl(WorkflowsSettings settings)
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

        Content = new StackPanel
        {
            Spacing = 8,
            Margin = new Thickness(4),
            Children = { _mcpEnabled, description },
        };
    }

    public bool Save()
    {
        _settings.SaveMcpEnabled(_mcpEnabled.IsChecked ?? true);
        return true;
    }
}
