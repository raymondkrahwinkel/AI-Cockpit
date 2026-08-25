using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Cockpit.Plugin.Kubernetes.Cluster;
using Cockpit.Plugin.Kubernetes.Model;

namespace Cockpit.Plugin.Kubernetes.Ui;

// One cluster's row in the settings view: label, a kubeconfig source (either a file path read live — e.g.
// `~/.kube/config` — or a pasted kubeconfig kept under the secret layer), a context picked from that file,
// allowed namespaces, and the off-by-default capability toggles. The pasted-kubeconfig box is never prefilled with
// a stored value — a blank box keeps what is already stored, a paste replaces it.
internal sealed class ClusterRowControl : UserControl
{
    private const string CurrentContextLabel = "(current-context)";

    private readonly string _id;
    private readonly bool _hasStoredKubeconfig;
    private readonly bool _hasStoredArgoToken;
    private readonly bool _usesExecAuth;
    private readonly TextBox _label;
    private readonly TextBox _kubeconfigPath;
    private readonly ComboBox _contextBox;
    private readonly TextBox _allowedNamespaces;
    private readonly TextBox _kubeconfig;
    private readonly TextBox _argoToken;
    private readonly CheckBox _allowClusterScoped;
    private readonly CheckBox _allowExec;
    private readonly CheckBox _allowPortForward;

    public event Action? RemoveRequested;

    public ClusterRowControl(ClusterRegistration? existing, bool hasStoredKubeconfig, bool hasStoredArgoToken = false)
    {
        _id = existing?.Id ?? Guid.NewGuid().ToString("n");
        _hasStoredKubeconfig = hasStoredKubeconfig;
        _hasStoredArgoToken = hasStoredArgoToken;
        _usesExecAuth = existing?.UsesExecAuth ?? false;

        _label = new TextBox { Text = existing?.Label ?? string.Empty, PlaceholderText = "Label (e.g. prod, staging)" };
        _kubeconfigPath = new TextBox { Text = existing?.KubeconfigPath ?? string.Empty, PlaceholderText = "Kubeconfig file, e.g. ~/.kube/config (leave blank to paste one below)" };
        _contextBox = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
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
                : "Or paste the kubeconfig for this cluster",
        };
        _argoToken = new TextBox
        {
            PasswordChar = '•',
            PlaceholderText = hasStoredArgoToken
                ? "Leave blank to keep the stored token, or paste a new one to replace it"
                : "Optional: an Argo CD read-only project-role token, to let the argo_* tools reach Argo's own API",
        };
        _allowClusterScoped = new CheckBox { Content = "Allow cluster-scoped resources (nodes, PVs, namespaces, cluster roles)", IsChecked = existing?.AllowClusterScoped ?? false };
        _allowExec = new CheckBox { Content = "Allow exec (run a command in a pod)", IsChecked = existing?.AllowExec ?? false };
        _allowPortForward = new CheckBox { Content = "Allow port-forward (open a tunnel into the cluster)", IsChecked = existing?.AllowPortForward ?? false };

        _RebuildContextItems([], existing?.ContextName);

        var browse = new Button { Content = "Browse…" };
        browse.Click += async (_, _) => await _BrowseAsync();

        var load = new Button { Content = "Load contexts", Margin = new Thickness(0, 2, 0, 0) };
        load.Click += (_, _) => _LoadContexts();

        var remove = new Button { Content = "Remove cluster", Margin = new Thickness(0, 4, 0, 0) };
        remove.Click += (_, _) => RemoveRequested?.Invoke();

        var pathRow = new DockPanel();
        DockPanel.SetDock(browse, Dock.Right);
        browse.Margin = new Thickness(6, 0, 0, 0);
        pathRow.Children.Add(browse);
        pathRow.Children.Add(_kubeconfigPath);

        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(_Hint("Cluster"));
        panel.Children.Add(_label);
        panel.Children.Add(_Hint("Kubeconfig file (read live on connect) — or paste one lower down"));
        panel.Children.Add(pathRow);
        panel.Children.Add(load);
        panel.Children.Add(_Hint("Context"));
        panel.Children.Add(_contextBox);

        // Warn, up front, when this context authenticates via an exec credential plugin — connecting runs an external
        // process, so the operator should know the kubeconfig is trusted.
        if (_usesExecAuth)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "⚠ This context authenticates with an exec credential plugin — connecting runs an external command. Only use a kubeconfig you trust.",
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = _Brush("CockpitStatusWaitingBrush", "#E0A33E"),
            });
        }

        panel.Children.Add(_Hint("Allowed namespaces"));
        panel.Children.Add(_allowedNamespaces);
        panel.Children.Add(_Hint("Paste a kubeconfig instead (stored under the secret layer)"));
        panel.Children.Add(_kubeconfig);
        panel.Children.Add(_Hint("Argo CD API token (stored under the secret layer, same as the kubeconfig)"));
        panel.Children.Add(_argoToken);
        panel.Children.Add(_Hint("Extra capabilities — off by default; each reaches past the namespace boundary"));
        panel.Children.Add(_allowClusterScoped);
        panel.Children.Add(_allowExec);
        panel.Children.Add(_allowPortForward);
        panel.Children.Add(remove);

        Content = new Border { Padding = new Thickness(0, 8, 0, 12), Child = panel };
    }

    public string Id => _id;

    // A row is blank — and dropped on Save — only when nothing was entered and nothing is already stored for it.
    public bool IsBlank =>
        string.IsNullOrWhiteSpace(_label.Text)
        && string.IsNullOrWhiteSpace(_kubeconfigPath.Text)
        && string.IsNullOrWhiteSpace(_kubeconfig.Text)
        && string.IsNullOrWhiteSpace(_allowedNamespaces.Text)
        && !_hasStoredKubeconfig
        && !_hasStoredArgoToken;

    // The kubeconfig file path, if the operator set one — stored as metadata and read live on connect.
    public string KubeconfigPath => (_kubeconfigPath.Text ?? string.Empty).Trim();

    // What was pasted into the kubeconfig box this session, if anything — the parent stores it through the secret layer.
    public string KubeconfigInput => _kubeconfig.Text ?? string.Empty;

    // What was pasted into the Argo token box this session, if anything — the parent stores it through the secret layer.
    public string ArgoTokenInput => _argoToken.Text ?? string.Empty;

    public ClusterRegistration ToRegistration() => new(
        Id: _id,
        Label: (_label.Text ?? string.Empty).Trim(),
        ContextName: _SelectedContext(),
        AllowedNamespaces: _ParseNamespaces(_allowedNamespaces.Text),
        AllowClusterScoped: _allowClusterScoped.IsChecked ?? false,
        AllowExec: _allowExec.IsChecked ?? false,
        AllowPortForward: _allowPortForward.IsChecked ?? false,
        // attach is model+gate-ready but has no meaningful non-interactive MCP tool yet, so it stays off.
        AllowAttach: false,
        UsesExecAuth: _usesExecAuth,
        KubeconfigPath: KubeconfigPath);

    private async Task _BrowseAsync()
    {
        if (TopLevel.GetTopLevel(this) is not { } top)
        {
            return;
        }

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select a kubeconfig file",
            AllowMultiple = false,
        });

        if (files.FirstOrDefault()?.TryGetLocalPath() is { Length: > 0 } localPath)
        {
            _kubeconfigPath.Text = localPath;
            _LoadContexts();
        }
    }

    private void _LoadContexts()
    {
        var yaml = KubeconfigInspector.ReadYaml(_kubeconfigPath.Text, _kubeconfig.Text);
        if (yaml is null)
        {
            return;
        }

        _RebuildContextItems(KubeconfigInspector.ListContexts(yaml).Names, _SelectedContext());
    }

    private void _RebuildContextItems(IEnumerable<string> names, string? keep)
    {
        var items = new List<string> { CurrentContextLabel };
        items.AddRange(names.Where(name => name != CurrentContextLabel).Distinct(StringComparer.Ordinal));
        if (!string.IsNullOrEmpty(keep) && !items.Contains(keep))
        {
            items.Add(keep);
        }

        _contextBox.ItemsSource = items;
        _contextBox.SelectedItem = string.IsNullOrEmpty(keep) ? CurrentContextLabel : keep;
    }

    private string _SelectedContext() =>
        _contextBox.SelectedItem is string selected && selected != CurrentContextLabel ? selected : string.Empty;

    private static IReadOnlyList<string> _ParseNamespaces(string? text) =>
        (text ?? string.Empty)
            .Split([',', '\n', '\r', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static TextBlock _Hint(string text) => new() { Text = text, FontSize = 11, Opacity = 0.7, TextWrapping = TextWrapping.Wrap };

    // The host's theme brush, resolved at call time. The fallback hex is only reached with no
    // `Application` (designer, headless test) and is held equal to its token by the repository's theme
    // guard. The exec-auth warning used to be drawn in `Brushes.Orange`, which is nobody's amber.
    private static IBrush _Brush(string key, string fallbackHex) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is IBrush brush
            ? brush
            : new SolidColorBrush(Color.Parse(fallbackHex));
}
