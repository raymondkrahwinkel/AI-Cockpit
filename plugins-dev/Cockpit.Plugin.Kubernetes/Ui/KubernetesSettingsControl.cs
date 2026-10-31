using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugin.Kubernetes.Contracts;
using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.Plugin.Kubernetes.UI;

// The plugin's settings view (opened from the gear in the plugin manager): a manageable list of cluster rows
// (add/remove, each with its own kubeconfig, allowed namespaces and capability toggles) plus the MCP on/off
// toggle. Implements `IAsyncPluginSettingsView`, so the host renders the Save/Close footer and performs the write this
// view hands it (AC-1003) — the metadata to storage, each kubeconfig through the secret layer, and clearing the
// credential of any cluster that was removed.
//
// AC-1394: the cluster list itself, and every write, live in the backend part (Settings.ClusterSettingsChannel) —
// this view only loads and saves a snapshot over the plugin's channel, never referencing ClusterRegistration or
// KubernetesSettings directly.
internal sealed class KubernetesSettingsControl : UserControl, IAsyncPluginSettingsView
{
    private readonly ICockpitUiHost _host;
    private readonly StackPanel _clustersPanel;
    private readonly List<ClusterRowControl> _rows = [];
    private readonly CheckBox _mcpEnabled;
    private readonly TextBlock _loadStatus;
    private bool _loaded;

    public KubernetesSettingsControl(ICockpitUiHost host)
    {
        _host = host;
        _clustersPanel = new StackPanel { Spacing = 4 };

        var addCluster = new Button { Content = "+ Add cluster" };
        addCluster.Click += (_, _) => _AddRow(existing: null);

        _mcpEnabled = new CheckBox { Content = "Let sessions use the Kubernetes MCP tools", IsChecked = true };
        _loadStatus = _Hint("Loading the clusters…");

        // AC-1033: the `?` beside the heading, pointing at this plugin's own settings page — adding a cluster,
        // the file-vs-pasted kubeconfig, and the pitfall of a context left on "(current-context)".
        var clustersHeading = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { _Label("Kubernetes clusters"), host.CreateHelpHint("kubernetes", "adding-a-cluster") },
        };

        // No ScrollViewer here: the host dialog already wraps every settings view in one (with the window inset).
        // A ScrollViewer nested inside that one is measured with unbounded height and never scrolls, so its tail —
        // the MCP toggle — rendered under the Save/Close footer. The host owns the scroll; the view is just content.
        Content = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                clustersHeading,
                _loadStatus,
                _Hint("Each cluster is a kubeconfig kept under the secret layer. An agent never gets the kubeconfig — it reaches the cluster only through the gated MCP tools. Namespaces you list here are free to read; anything outside asks each session, and every change asks each time."),
                _clustersPanel,
                addCluster,
                _Label("MCP"),
                _mcpEnabled,
            },
        };

        // AC-1891: loaded off the constructor, so opening Options does not wait on the backend part. Until it is
        // in, the view is disabled and has nothing to save — no row can have been edited yet.
        IsEnabled = false;
        _ = _LoadAsync();
    }

    private async Task _LoadAsync()
    {
        try
        {
            var snapshot = await _LoadSnapshotAsync();
            if (snapshot.Clusters.Count == 0)
            {
                _AddRow(existing: null);
            }
            else
            {
                foreach (var cluster in snapshot.Clusters)
                {
                    _AddRow(cluster);
                }
            }

            _mcpEnabled.IsChecked = snapshot.McpEnabled;
            _loadStatus.IsVisible = false;
            _loaded = true;
            IsEnabled = true;
        }
        catch (Exception exception)
        {
            _loadStatus.Text = $"Could not load the clusters: {exception.Message}";
        }
    }

    private void _AddRow(ClusterSnapshot? existing)
    {
        var row = new ClusterRowControl(_host, existing);
        row.RemoveRequested += () =>
        {
            _rows.Remove(row);
            _clustersPanel.Children.Remove(row);
        };
        _rows.Add(row);
        _clustersPanel.Children.Add(row);
    }

    // AC-1004, criterion 3: the old `Save()` validated nothing and wrote everything. The one check it did make —
    // a cluster needs a label — it made by silently dropping the row, taking the operator's kubeconfig with it.
    // That is the half that belongs here now that a refusal can carry a reason; every write stays in `_Commit`.
    public bool TryStage(out Func<Task>? commit, out string? error)
    {
        if (!_loaded)
        {
            commit = null;
            error = null;
            return true;
        }

        // Numbered by position in the panel, since a row with no label has nothing else to be called by.
        var labelless = _rows.FindIndex(row => !row.IsBlank && string.IsNullOrWhiteSpace(row.ToEdit().Label));
        if (labelless >= 0)
        {
            commit = null;
            error = $"Cluster {labelless + 1} has no label — an agent names a cluster by it, and so does every "
                + "consent prompt. Give it one, or remove the row.";
            return false;
        }

        commit = _CommitAsync;
        error = null;
        return true;
    }

    // One channel round trip, writes included: the backend part stores each kubeconfig, detects exec-auth and
    // clears the orphans (Settings.ClusterSettingsChannel.SaveAsync). The host awaits it (AC-1891).
    private async Task _CommitAsync()
    {
        var edits = _rows.Where(row => !row.IsBlank).Select(row => row.ToEdit()).ToList();
        var request = new ClusterSettingsSaveRequest(edits, _mcpEnabled.IsChecked ?? true);
        var payload = JsonSerializer.SerializeToElement(request, KubernetesChannel.Json);
        await _host.Channel.InvokeAsync(KubernetesChannel.SaveClusterSettings, payload);
    }

    private async Task<ClusterSettingsSnapshot> _LoadSnapshotAsync()
    {
        var payload = JsonSerializer.SerializeToElement<object?>(null, KubernetesChannel.Json);
        var answer = await _host.Channel.InvokeAsync(KubernetesChannel.LoadClusterSettings, payload);
        return answer.Deserialize<ClusterSettingsSnapshot>(KubernetesChannel.Json)
            ?? new ClusterSettingsSnapshot([], McpEnabled: true);
    }

    private static TextBlock _Label(string text) => new() { Text = text, FontSize = 11, Margin = new Thickness(0, 6, 0, 0) };

    private static TextBlock _Hint(string text) => new() { Text = text, FontSize = 11, Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
}
