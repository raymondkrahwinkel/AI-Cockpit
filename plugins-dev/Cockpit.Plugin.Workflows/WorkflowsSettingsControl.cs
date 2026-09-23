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
    private readonly NumericUpDown _catchUpGrace;

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

        // AC-1359: how late a scheduled trigger may still run after a restart or a sleeping laptop found it. Past
        // this, the slot is logged as missed rather than run — the same honesty rule as AC-493's reminders.
        _catchUpGrace = new NumericUpDown
        {
            Minimum = 0,
            Maximum = 1440,
            Increment = 5,
            Value = settings.CatchUpGraceMinutes,
            Width = 120,
        };

        var catchUpLabel = new TextBlock { Text = "Catch up scheduled flows within (minutes)", VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        var catchUpRow = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 8,
            Children = { catchUpLabel, _catchUpGrace },
        };

        var catchUpDescription = new TextBlock
        {
            Text = "A scheduled flow the cockpit was not running to fire still runs once, late, within this window. "
                + "Past it, the slot is logged as missed instead — never run silently late.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11,
            Opacity = 0.7,
        };

        Content = new StackPanel
        {
            Spacing = 8,
            Margin = new Thickness(4),
            Children = { mcpRow, description, catchUpRow, catchUpDescription },
        };
    }

    // AC-1004, criterion 3: the MCP toggle is read fresh whenever the plugin is asked for its servers, so there is
    // nothing to re-register for it here. The grace is read fresh by the watcher on every tick, same reasoning.
    public bool TryStage(out Action? commit, out string? error)
    {
        commit = () =>
        {
            _settings.SaveMcpEnabled(_mcpEnabled.IsChecked ?? true);
            _settings.SaveCatchUpGraceMinutes((int)(_catchUpGrace.Value ?? WorkflowsSettings.DefaultCatchUpGraceMinutes));
        };
        error = null;
        return true;
    }
}
