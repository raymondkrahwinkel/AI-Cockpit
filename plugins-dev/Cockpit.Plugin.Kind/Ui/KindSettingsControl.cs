using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugin.Kind.Settings;

namespace Cockpit.Plugin.Kind.Ui;

// The plugin's settings view. The registry is agent-managed (kind_create/kind_delete), so this panel is read-only
// except for the two operator-only controls: Pinned, which no agent has a tool to set (AC-179 D2), and the maximum
// lifetime. `IPluginSettingsView`, so the host renders the Save/Close footer and does the write (AC-1003).
internal sealed class KindSettingsControl : UserControl, IPluginSettingsView
{
    private readonly KindSettings _settings;
    private readonly List<(KindClusterRecord Record, CheckBox Pinned)> _rows = [];
    private readonly NumericUpDown _maxLifetimeHours;
    private readonly CheckBox _mcpEnabled;

    public KindSettingsControl(ICockpitHost host, KindSettings settings)
    {
        _settings = settings;

        var clustersPanel = new StackPanel { Spacing = 4 };
        foreach (var record in settings.KindClusters)
        {
            var pinned = new CheckBox { Content = "Pinned (kept even if the owning session closes or its lifetime expires)", IsChecked = record.IsPinned };
            _rows.Add((record, pinned));
            clustersPanel.Children.Add(new Border
            {
                Padding = new Thickness(0, 4, 0, 8),
                Child = new StackPanel
                {
                    Spacing = 2,
                    Children =
                    {
                        new TextBlock { Text = record.Name, FontWeight = FontWeight.Bold },
                        _Hint($"owner {record.OwnerPaneId} · created {record.CreatedAt:yyyy-MM-dd HH:mm} · {record.KubeconfigPath}"),
                        pinned,
                    },
                },
            });
        }

        _maxLifetimeHours = new NumericUpDown
        {
            Value = (decimal)settings.KindClusterMaxLifetime.TotalHours,
            Minimum = 1,
            Maximum = 168,
            Increment = 1,
            FormatString = "0",
            Width = 120,
        };

        _mcpEnabled = new CheckBox { Content = "Let sessions use the kind MCP tools", IsChecked = settings.McpEnabled };

        // No ScrollViewer here: the host dialog already wraps every settings view in one.
        Content = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children = { _Label("Kind clusters"), host.CreateHelpHint("kind", "teardown") },
                },
                _Hint("Disposable local clusters an agent spun up with kind_create. Torn down automatically when the owning session closes, the cockpit exits, or the lifetime below expires — pin one to keep it regardless."),
                clustersPanel.Children.Count == 0 ? _Hint("None right now.") : clustersPanel,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    Children = { new TextBlock { Text = "Maximum lifetime (hours)", VerticalAlignment = VerticalAlignment.Center }, _maxLifetimeHours },
                },
                _Label("MCP"),
                _mcpEnabled,
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
        // Pinned is the only field this view can change on a record — everything else (name, owner, kubeconfig
        // path) is set at kind_create time and stays that way.
        _settings.KindClusters = _rows.Select(row => row.Record with { IsPinned = row.Pinned.IsChecked ?? false }).ToList();
        _settings.KindClusterMaxLifetime = TimeSpan.FromHours((double)(_maxLifetimeHours.Value ?? 4m));
        _settings.McpEnabled = _mcpEnabled.IsChecked ?? true;
    }

    private static TextBlock _Label(string text) => new() { Text = text, FontSize = 11, Margin = new Thickness(0, 6, 0, 0) };

    private static TextBlock _Hint(string text) => new() { Text = text, FontSize = 11, Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
}
