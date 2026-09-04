using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugin.Kubernetes.Cluster;
using Cockpit.Plugin.Kubernetes.Model;
using Cockpit.Plugin.Kubernetes.Settings;

namespace Cockpit.Plugin.Kubernetes.Ui;

// The plugin's settings view (opened from the gear in the plugin manager): a manageable list of cluster rows
// (add/remove, each with its own kubeconfig, allowed namespaces and capability toggles) plus the MCP on/off
// toggle. Implements `IPluginSettingsView`, so the host renders the Save/Close footer and performs the write this
// view hands it (AC-1003) — the metadata to storage, each kubeconfig through the secret layer, and clearing the
// credential of any cluster that was removed.
internal sealed class KubernetesSettingsControl : UserControl, IPluginSettingsView
{
    private readonly KubernetesSettings _settings;
    private readonly StackPanel _clustersPanel;
    private readonly List<ClusterRowControl> _rows = [];
    private readonly CheckBox _mcpEnabled;
    private readonly IReadOnlyList<string> _originalClusterIds;

    public KubernetesSettingsControl(ICockpitHost host, KubernetesSettings settings)
    {
        _settings = settings;
        _clustersPanel = new StackPanel { Spacing = 4 };

        var clusters = settings.Clusters;
        _originalClusterIds = clusters.Select(cluster => cluster.Id).ToList();
        if (clusters.Count == 0)
        {
            _AddRow(existing: null, hasStoredKubeconfig: false, hasStoredArgoToken: false);
        }
        else
        {
            foreach (var cluster in clusters)
            {
                _AddRow(cluster, settings.GetKubeconfig(cluster.Id) is not null, settings.GetArgoToken(cluster.Id) is not null);
            }
        }

        var addCluster = new Button { Content = "+ Add cluster" };
        addCluster.Click += (_, _) => _AddRow(existing: null, hasStoredKubeconfig: false, hasStoredArgoToken: false);

        _mcpEnabled = new CheckBox { Content = "Let sessions use the Kubernetes MCP tools", IsChecked = settings.McpEnabled };

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
                _Hint("Each cluster is a kubeconfig kept under the secret layer. An agent never gets the kubeconfig — it reaches the cluster only through the gated MCP tools. Namespaces you list here are free to read; anything outside asks each session, and every change asks each time."),
                _clustersPanel,
                addCluster,
                _Label("MCP"),
                _mcpEnabled,
            },
        };
    }

    private void _AddRow(ClusterRegistration? existing, bool hasStoredKubeconfig, bool hasStoredArgoToken)
    {
        var row = new ClusterRowControl(existing, hasStoredKubeconfig, hasStoredArgoToken);
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
    public bool TryStage(out Action? commit, out string? error)
    {
        // Numbered by position in the panel, since a row with no label has nothing else to be called by.
        var labelless = _rows.FindIndex(row => !row.IsBlank && string.IsNullOrWhiteSpace(row.ToRegistration().Label));
        if (labelless >= 0)
        {
            commit = null;
            error = $"Cluster {labelless + 1} has no label — an agent names a cluster by it, and so does every "
                + "consent prompt. Give it one, or remove the row.";
            return false;
        }

        commit = _Commit;
        error = null;
        return true;
    }

    // Whole body, writes included: this one stores each row's kubeconfig as it walks the list (and clears the
    // orphans afterwards), so splitting the writes out of it would mean reading the effective kubeconfig twice —
    // once to validate, once to store — for nothing. Nothing here runs before the operator confirms.
    private void _Commit()
    {
        var kept = _rows.Where(row => !row.IsBlank).ToList();

        var registrations = new List<ClusterRegistration>();
        foreach (var row in kept)
        {
            var registration = row.ToRegistration();
            var pasted = row.KubeconfigInput.Trim();
            if (!string.IsNullOrEmpty(registration.KubeconfigPath))
            {
                // The path model owns the source — drop any stored secret so a later cleared path cannot silently
                // revive a stale kubeconfig.
                _settings.ClearKubeconfig(row.Id);
            }
            else if (pasted.Length > 0)
            {
                _settings.SetKubeconfig(row.Id, pasted);
            }

            var pastedToken = row.ArgoTokenInput.Trim();
            if (pastedToken.Length > 0)
            {
                _settings.SetArgoToken(row.Id, pastedToken);
            }

            // Detect exec-auth on the effective kubeconfig (the file at the path, or the pasted/stored content) so
            // the row can warn that connecting will run an external process.
            var content = pasted.Length > 0 ? pasted : _settings.GetKubeconfig(row.Id);
            var effectiveKubeconfig = KubeconfigInspector.ReadYaml(registration.KubeconfigPath, content);
            if (effectiveKubeconfig is { Length: > 0 })
            {
                registration = registration with { UsesExecAuth = KubeconfigInspector.Inspect(effectiveKubeconfig, registration.ContextName).UsesExecAuth };
            }

            registrations.Add(registration);
        }

        // Clear the stored kubeconfig of any cluster that is no longer saved — removed, or emptied out until the
        // row counted as blank — so an orphaned secret does not linger.
        var savedIds = registrations.Select(registration => registration.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var goneId in _originalClusterIds.Where(id => !savedIds.Contains(id)))
        {
            _settings.ClearKubeconfig(goneId);
            _settings.ClearArgoToken(goneId);
        }

        _settings.Clusters = registrations;
        _settings.McpEnabled = _mcpEnabled.IsChecked ?? true;
    }

    private static TextBlock _Label(string text) => new() { Text = text, FontSize = 11, Margin = new Thickness(0, 6, 0, 0) };

    private static TextBlock _Hint(string text) => new() { Text = text, FontSize = 11, Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
}
