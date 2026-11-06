using Avalonia.Controls;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugin.Docker.Settings;

namespace Cockpit.Plugin.Docker.UI;

// The plugin's settings view (all code-behind Avalonia, like the other plugins). The host renders the Save/Close
// footer and wraps this in its own ScrollViewer, so we do not nest one.
internal sealed class DockerSettingsControl : UserControl, IPluginSettingsView
{
    private readonly DockerSettings _settings;
    private readonly CheckBox _mcpEnabled;
    private readonly CheckBox _allowExec;
    private readonly ComboBox _consentMode;

    public DockerSettingsControl(DockerSettings settings)
    {
        _settings = settings;

        _mcpEnabled = new CheckBox
        {
            Content = "Offer the cockpit-docker MCP server to sessions",
            IsChecked = settings.McpEnabled,
        };

        _allowExec = new CheckBox
        {
            Content = "Allow exec / run into containers (dangerous — off by default)",
            IsChecked = settings.AllowExec,
        };

        // AC-1348: index matches DockerConsentMode's declaration order (AlwaysAsk/ReadFree/AllFree).
        _consentMode = new ComboBox
        {
            ItemsSource = new[] { "Always ask", "Read-free (reads skip the card; changes still ask)", "All-free (nothing asks on this daemon)" },
            SelectedIndex = (int)settings.ConsentMode,
        };

        Content = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                _mcpEnabled,
                _allowExec,
                new TextBlock
                {
                    Text = "Remote Docker hosts are not supported yet. SDK operations use the local default socket; CLI operations use your Docker CLI environment and context. Saved endpoint overrides are ignored.",
                    Opacity = 0.7,
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                },
                new TextBlock { Text = "Consent mode for this daemon", Margin = new(0, 8, 0, 0) },
                _consentMode,
                new TextBlock
                {
                    Text = "Approvals saved for a previous remote endpoint do not apply to the local daemon.",
                    Opacity = 0.7,
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                },
            },
        };
    }

    public bool TryStage(out Action? commit, out string? error)
    {
        commit = _Commit;
        error = null;
        return true;
    }

    private void _Commit()
    {
        _settings.McpEnabled = _mcpEnabled.IsChecked ?? true;
        _settings.AllowExec = _allowExec.IsChecked ?? false;
        _settings.ConsentMode = (DockerConsentMode)_consentMode.SelectedIndex;
    }
}
