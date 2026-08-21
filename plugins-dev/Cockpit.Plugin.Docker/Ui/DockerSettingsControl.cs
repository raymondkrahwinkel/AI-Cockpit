using Avalonia.Controls;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugin.Docker.Settings;

namespace Cockpit.Plugin.Docker.Ui;

// The plugin's settings view (all code-behind Avalonia, like the other plugins). The host renders the Save/Close
// footer and wraps this in its own ScrollViewer, so we do not nest one.
internal sealed class DockerSettingsControl : UserControl, IPluginSettingsView
{
    private readonly DockerSettings _settings;
    private readonly CheckBox _mcpEnabled;
    private readonly CheckBox _allowExec;
    private readonly TextBox _endpoint;

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

        _endpoint = new TextBox
        {
            PlaceholderText = "Blank = local default socket",
            Text = settings.DaemonEndpoint,
        };

        Content = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                _mcpEnabled,
                _allowExec,
                new TextBlock { Text = "Docker daemon endpoint", Margin = new(0, 8, 0, 0) },
                _endpoint,
                new TextBlock
                {
                    Text = "e.g. npipe://./pipe/docker_engine (Windows) or unix:///var/run/docker.sock. Leave blank to use the local default.",
                    Opacity = 0.7,
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                },
            },
        };
    }

    // AC-1004, criterion 3: the old `Save()` was these three property writes and nothing else. The side effect a
    // saved endpoint needs — the Docker engine's cached client being dropped (`DockerPlugin` wires
    // `engine.Invalidate`) — hangs on `ICockpitHost.OnSettingsSaved`, which the host raises after this write, so
    // the rebuilt client reads the new endpoint rather than the one it just replaced.
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
        _settings.DaemonEndpoint = (_endpoint.Text ?? string.Empty).Trim();
    }
}
