using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugin.Kubernetes.Cluster;
using Cockpit.Plugin.Kubernetes.Model;
using Cockpit.Plugin.Kubernetes.Settings;

namespace Cockpit.Plugin.Kubernetes.Ui;

/// <summary>
/// The plugin's settings view (opened from the gear in the plugin manager): a manageable list of cluster rows
/// (add/remove, each with its own kubeconfig, allowed namespaces and capability toggles) plus the MCP on/off
/// toggle. Implements <see cref="IPluginSettingsView"/>, so the host renders the Save/Close footer and
/// <see cref="Save"/> persists on Save — the metadata to storage, each kubeconfig through the secret layer, and
/// clears the credential of any cluster that was removed.
/// </summary>
internal sealed class KubernetesSettingsControl : UserControl, IPluginSettingsView
{
    private readonly KubernetesSettings _settings;
    private readonly StackPanel _clustersPanel;
    private readonly List<ClusterRowControl> _rows = [];
    private readonly CheckBox _mcpEnabled;
    private readonly IReadOnlyList<string> _originalClusterIds;

    public KubernetesSettingsControl(KubernetesSettings settings)
    {
        _settings = settings;
        _clustersPanel = new StackPanel { Spacing = 4 };

        var clusters = settings.Clusters;
        _originalClusterIds = clusters.Select(cluster => cluster.Id).ToList();
        if (clusters.Count == 0)
        {
            _AddRow(existing: null, hasStoredKubeconfig: false);
        }
        else
        {
            foreach (var cluster in clusters)
            {
                _AddRow(cluster, settings.GetKubeconfig(cluster.Id) is not null);
            }
        }

        var addCluster = new Button { Content = "+ Add cluster" };
        addCluster.Click += (_, _) => _AddRow(existing: null, hasStoredKubeconfig: false);

        _mcpEnabled = new CheckBox { Content = "Let sessions use the Kubernetes MCP tools", IsChecked = settings.McpEnabled };

        Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 8,
                Children =
                {
                    _Label("Kubernetes clusters"),
                    _Hint("Each cluster is a kubeconfig kept under the secret layer. An agent never gets the kubeconfig — it reaches the cluster only through the gated MCP tools. Namespaces you list here are free to read; anything outside asks each session, and every change asks each time."),
                    _clustersPanel,
                    addCluster,
                    _Label("MCP"),
                    _mcpEnabled,
                },
            },
        };
    }

    private void _AddRow(ClusterRegistration? existing, bool hasStoredKubeconfig)
    {
        var row = new ClusterRowControl(existing, hasStoredKubeconfig);
        row.RemoveRequested += () =>
        {
            _rows.Remove(row);
            _clustersPanel.Children.Remove(row);
        };
        _rows.Add(row);
        _clustersPanel.Children.Add(row);
    }

    public bool Save()
    {
        var kept = _rows.Where(row => !row.IsBlank).ToList();

        var registrations = new List<ClusterRegistration>();
        foreach (var row in kept)
        {
            var registration = row.ToRegistration();

            // A cluster needs a label — it is how an agent names it and how a consent prompt identifies it. Skip a
            // labelless row rather than persist an empty-named cluster.
            if (string.IsNullOrWhiteSpace(registration.Label))
            {
                continue;
            }

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

        // Clear the stored kubeconfig of any cluster that is no longer saved — removed, or its label cleared so it
        // was dropped above — so an orphaned secret does not linger.
        var savedIds = registrations.Select(registration => registration.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var goneId in _originalClusterIds.Where(id => !savedIds.Contains(id)))
        {
            _settings.ClearKubeconfig(goneId);
        }

        _settings.Clusters = registrations;
        _settings.McpEnabled = _mcpEnabled.IsChecked ?? true;
        return true;
    }

    private static TextBlock _Label(string text) => new() { Text = text, FontSize = 11, Margin = new Thickness(0, 6, 0, 0) };

    private static TextBlock _Hint(string text) => new() { Text = text, FontSize = 11, Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
}
