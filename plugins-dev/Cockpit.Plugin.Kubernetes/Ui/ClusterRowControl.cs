using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Cockpit.Plugin.Kubernetes.Model;

namespace Cockpit.Plugin.Kubernetes.Ui;

/// <summary>
/// One cluster's row in the settings view: label, kubeconfig context, allowed namespaces, the pasted kubeconfig
/// (kept out of the metadata and stored through the secret layer by the parent's Save), and the off-by-default
/// capability toggles. The kubeconfig box is never prefilled with a stored value — a blank box keeps what is
/// already stored, a paste replaces it — so the settings view does not render a credential it does not need to.
/// </summary>
internal sealed class ClusterRowControl : UserControl
{
    private readonly string _id;
    private readonly bool _hasStoredKubeconfig;
    private readonly bool _usesExecAuth;
    private readonly TextBox _label;
    private readonly TextBox _contextName;
    private readonly TextBox _allowedNamespaces;
    private readonly TextBox _kubeconfig;
    private readonly CheckBox _allowClusterScoped;
    private readonly CheckBox _allowExec;

    public event Action? RemoveRequested;

    public ClusterRowControl(ClusterRegistration? existing, bool hasStoredKubeconfig)
    {
        _id = existing?.Id ?? Guid.NewGuid().ToString("n");
        _hasStoredKubeconfig = hasStoredKubeconfig;
        _usesExecAuth = existing?.UsesExecAuth ?? false;

        _label = new TextBox { Text = existing?.Label ?? string.Empty, PlaceholderText = "Label (e.g. prod, staging)" };
        _contextName = new TextBox { Text = existing?.ContextName ?? string.Empty, PlaceholderText = "kubeconfig context (blank = current-context)" };
        _allowedNamespaces = new TextBox
        {
            Text = existing is null ? string.Empty : string.Join(", ", existing.AllowedNamespaces),
            PlaceholderText = "Allowed namespaces, comma-separated (e.g. default, my-app). A namespace outside this list asks each session.",
            AcceptsReturn = false,
        };
        _kubeconfig = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 96,
            PlaceholderText = hasStoredKubeconfig
                ? "Leave blank to keep the stored kubeconfig, or paste a new one to replace it"
                : "Paste the kubeconfig for this cluster",
        };
        _allowClusterScoped = new CheckBox { Content = "Allow cluster-scoped resources (nodes, PVs, namespaces, cluster roles)", IsChecked = existing?.AllowClusterScoped ?? false };
        _allowExec = new CheckBox { Content = "Allow exec (run a command in a pod)", IsChecked = existing?.AllowExec ?? false };

        var remove = new Button { Content = "Remove cluster", Margin = new Thickness(0, 4, 0, 0) };
        remove.Click += (_, _) => RemoveRequested?.Invoke();

        var panel = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                _Hint("Cluster"),
                _label,
                _contextName,
            },
        };

        // Warn, up front, when this context authenticates via an exec credential plugin — connecting will run an
        // external process, so the operator should know the kubeconfig is trusted.
        if (_usesExecAuth)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "⚠ This context authenticates with an exec credential plugin — connecting runs an external command. Only use a kubeconfig you trust.",
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.Orange,
            });
        }

        panel.Children.Add(_Hint("Allowed namespaces"));
        panel.Children.Add(_allowedNamespaces);
        panel.Children.Add(_Hint("Kubeconfig (stored under the secret layer)"));
        panel.Children.Add(_kubeconfig);
        panel.Children.Add(_Hint("Extra capabilities — off by default; each reaches past the namespace boundary"));
        panel.Children.Add(_allowClusterScoped);
        panel.Children.Add(_allowExec);
        panel.Children.Add(remove);

        Content = new Border
        {
            Padding = new Thickness(0, 8, 0, 12),
            Child = panel,
        };
    }

    public string Id => _id;

    /// <summary>A row is blank — and dropped on Save — only when nothing was entered and nothing is already stored for it.</summary>
    public bool IsBlank =>
        string.IsNullOrWhiteSpace(_label.Text)
        && string.IsNullOrWhiteSpace(_kubeconfig.Text)
        && string.IsNullOrWhiteSpace(_allowedNamespaces.Text)
        && !_hasStoredKubeconfig;

    /// <summary>What was pasted into the kubeconfig box this session, if anything — the parent stores it through the secret layer.</summary>
    public string KubeconfigInput => _kubeconfig.Text ?? string.Empty;

    public ClusterRegistration ToRegistration() => new(
        Id: _id,
        Label: (_label.Text ?? string.Empty).Trim(),
        ContextName: (_contextName.Text ?? string.Empty).Trim(),
        AllowedNamespaces: _ParseNamespaces(_allowedNamespaces.Text),
        AllowClusterScoped: _allowClusterScoped.IsChecked ?? false,
        AllowExec: _allowExec.IsChecked ?? false,
        // port-forward and attach are model+gate-ready but not surfaced in v1 (their safe form needs an
        // operator-facing kill-switch), so they stay off until the follow-up wires their tools and toggles.
        AllowPortForward: false,
        AllowAttach: false,
        UsesExecAuth: _usesExecAuth);

    private static IReadOnlyList<string> _ParseNamespaces(string? text) =>
        (text ?? string.Empty)
            .Split([',', '\n', '\r', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static TextBlock _Hint(string text) => new() { Text = text, FontSize = 11, Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
}
