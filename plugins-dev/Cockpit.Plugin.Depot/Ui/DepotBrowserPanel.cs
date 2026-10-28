using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Cockpit.Plugin.Depot.Contracts;
using Cockpit.Plugins.Abstractions.UI;
using Material.Icons;
using Material.Icons.Avalonia;

namespace Cockpit.Plugin.Depot.UI;

// AC-1568: browse a connected Depot project — documents and artifacts in one tree, view or download either, and for
// an artifact manage its share link. The behaviour is DepotBrowserModel's; this is only the controls.
internal sealed partial class DepotBrowserPanel : UserControl
{
    private readonly ICockpitUiHost _host;
    private readonly DepotSettings? _settings;
    private readonly Dictionary<string, TextBlock> _openBadges = new(StringComparer.Ordinal);
    private readonly Button _openIn = new() { Name = "OpenFilesInButton", Classes = { "Compact" }, Content = new MaterialIcon { Kind = MaterialIconKind.CogOutline, Width = 14, Height = 14 } };
    private readonly IReadOnlyList<DepotConnectionRegistration> _connections;
    private readonly HttpClient _http = new();
    private readonly ComboBox _connectionBox = new() { MinWidth = 140 };
    private readonly ComboBox _projectBox = new() { MinWidth = 200 };
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _signIn = new() { IsVisible = false };
    private readonly Button _upload = new() { Content = "Upload", IsVisible = false };
    private readonly Button _newMarkdown = new() { Content = "New Markdown file", IsVisible = false };
    private readonly Button _publishFolder = new() { Content = "Publish folder here…", IsVisible = false };
    private readonly StackPanel _publications = new() { Spacing = 6, Margin = new Thickness(12, 6) };
    private readonly TreeView _tree = new();
    private readonly StackPanel _detail = new() { Spacing = 6, Margin = new Thickness(12, 10) };
    private readonly Border _detailContainer = new() { IsVisible = false };
    private readonly StackPanel _state = new() { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 300 };
    private readonly MaterialIcon _stateIcon = new() { Width = 28, Height = 28, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _stateTitle = new() { FontSize = 14, FontWeight = FontWeight.SemiBold, TextAlignment = TextAlignment.Center };
    private DepotBrowserModel? _model;
    private DepotBrowserTree _loaded = new([], false);
    private string? _project;
    private bool _filling;

    // AC-1586: `inbox` is the plugin's shared inbox model (the badge's), or null for a panel that keeps its own;
    // `openInbox` starts on the Inbox tab, as the side-menu button does.
    public DepotBrowserPanel(ICockpitUiHost host, IReadOnlyList<DepotConnectionRegistration> connections, DepotInboxModel? inbox = null, bool openInbox = false)
    {
        _host = host;
        _settings = new DepotSettings(host.Storage);
        _connections = connections;
        var refresh = _Button("Refresh");
        refresh.Click += async (_, _) => await LoadTreeAsync().ConfigureAwait(true);
        _signIn.Click += async (_, _) => await SignInAsync().ConfigureAwait(true);
        _connectionBox.ItemsSource = connections.Select(connection => connection.Name).ToList();
        _connectionBox.IsVisible = connections.Count > 1;
        _connectionBox.SelectionChanged += async (_, _) => await SelectConnectionAsync(_connectionBox.SelectedIndex).ConfigureAwait(true);
        _projectBox.SelectionChanged += async (_, _) =>
        {
            if (!_filling && _projectBox.SelectedItem is DepotBrowserProject project)
            {
                _project = project.Project;
                await LoadTreeAsync().ConfigureAwait(true);
            }
        };
        _tree.SelectionChanged += async (_, _) =>
        {
            if (_tree.SelectedItem is TreeViewItem { Tag: DepotTreeItem item })
            {
                await ShowAsync(item).ConfigureAwait(true);
            }
        };

        _upload.Click += async (_, _) => await UploadAsync().ConfigureAwait(true);
        _publishFolder.Click += async (_, _) => await PublishFolderAsync().ConfigureAwait(true);
        _newMarkdown.Click += async (_, _) => await _NewMarkdownAsync(_SelectedFolder()).ConfigureAwait(true);
        _ListenForFileSaves();
        _ListenForOpenWorkspaces();
        _openIn.Flyout = _OpenInMenu();
        ToolTip.SetTip(_openIn, "Open files in");
        var layout = _FilesLayout(refresh);
        var ownsInbox = inbox is null;
        var inboxModel = inbox ?? new DepotInboxModel(host);
        if (ownsInbox)
        {
            DetachedFromVisualTree += (_, _) => inboxModel.Dispose();
        }

        Content = _Tabs(layout, new DepotInboxPanel(inboxModel, connections), openInbox);

        if (connections.Count == 0)
        {
            _SetState("No Depot server connected", "Add a Depot server in the Depot settings first.", MaterialIconKind.DatabaseOffOutline);
            return;
        }

        _connectionBox.SelectedIndex = 0;
    }

    // The real panel over fixed data for plugin screenshot scenes. Controls are deliberately inert: scenes never
    // call the host or Depot, but they exercise this panel's actual layout and detail composition.
    internal DepotBrowserPanel(DepotBrowserSceneModel scene)
    {
        _host = null!;
        _connections = [];
        var refresh = _Button("Refresh");
        _connectionBox.IsVisible = false;
        _projectBox.ItemsSource = new[] { scene.Project };
        _projectBox.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<DepotBrowserProject>((project, _) => new TextBlock { Text = project?.Name });
        _projectBox.SelectedItem = scene.Project;
        _project = scene.Project.Project;
        _loaded = scene.Tree;
        _upload.IsVisible = scene.Tree.CanShare;
        _newMarkdown.IsVisible = scene.Tree.CanShare;
        _publishFolder.IsVisible = scene.Tree.CanShare;
        Content = _Tabs(_FilesLayout(refresh), new TextBlock { Text = "Inbox", Margin = new Thickness(12) }, false);

        var items = DepotTreeBuilder.Build(scene.Tree.Nodes);
        _tree.ItemsSource = items.Select(item => _Row(item, scene.SelectedPath)).ToList();
        _openIn.Flyout = _OpenInMenu();
        foreach (var (path, text) in scene.Badges ?? new Dictionary<string, string>())
        {
            if (_openBadges.TryGetValue(path, out var badge))
            {
                badge.Text = text;
            }
        }
        if (scene.Tree.Nodes.Count == 0)
        {
            _SetState("No Depot files yet", "This project has no documents or artifacts yet.", MaterialIconKind.FolderOpenOutline);
        }
        else
        {
            _SetState(null, null, MaterialIconKind.FolderOpenOutline);
            if (_Find(items, scene.SelectedPath) is { } selected)
            {
                _ShowScene(selected, scene.Excerpt);
            }
        }
    }

    private Control _FilesLayout(Button refresh)
    {
        _AddTreeSelectionStyles();
        foreach (var button in new[] { _signIn, _upload, _publishFolder, _newMarkdown })
        {
            button.Classes.Add("Compact");
        }

        var top = new WrapPanel { Margin = new Thickness(12, 8, 6, 2) };
        foreach (var control in new Control[] { _connectionBox, _projectBox, refresh, _newMarkdown, _upload, _publishFolder, _openIn })
        {
            control.Margin = new Thickness(0, 0, 6, 6);
            top.Children.Add(control);
        }

        _message.TextWrapping = TextWrapping.Wrap;
        _message.TextAlignment = TextAlignment.Center;
        _message.Foreground = _Brush("CockpitTextSecondaryBrush");
        _state.Children.Add(_stateIcon);
        _state.Children.Add(_stateTitle);
        _state.Children.Add(_message);
        _state.Children.Add(_signIn);

        var treeLayer = new Grid();
        treeLayer.Children.Add(_tree);
        treeLayer.Children.Add(_state);
        var body = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        body.Children.Add(new Border
        {
            BorderBrush = _Brush("CockpitHairlineBrush"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = treeLayer,
        });
        _detailContainer.Background = _Brush("CockpitPanelBgBrush");
        _detailContainer.BorderBrush = _Brush("CockpitHairlineBrush");
        _detailContainer.BorderThickness = new Thickness(0, 1, 0, 0);
        _detailContainer.MaxHeight = 260;
        _detailContainer.Child = new ScrollViewer { Content = _detail };
        Grid.SetRow(_detailContainer, 1);
        body.Children.Add(_detailContainer);

        var layout = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(_publications, Dock.Bottom);
        layout.Children.Add(top);
        layout.Children.Add(_publications);
        layout.Children.Add(body);
        return layout;
    }

    private void _AddTreeSelectionStyles()
    {
        foreach (var state in new[] { new[] { ":selected" }, new[] { ":selected", ":pointerover" }, new[] { ":pressed", ":selected", ":pointerover" } })
        {
            _tree.Styles.Add(new Style(selector =>
            {
                var item = selector.OfType<TreeViewItem>();
                foreach (var pseudoClass in state)
                {
                    item = item.Class(pseudoClass);
                }

                return item.Template().OfType<Border>().Name("PART_LayoutRoot");
            })
            {
                Setters = { new Setter(Border.BackgroundProperty, _Brush("CockpitAccentSelectionBrush")) },
            });
        }
    }

    private static TabControl _Tabs(Control files, Control inbox, bool openInbox)
    {
        var inboxTab = new TabItem { Header = "Inbox", Content = inbox };
        return new TabControl
        {
            Items = { new TabItem { Header = "Files", Content = files }, inboxTab },
            SelectedItem = openInbox ? inboxTab : null,
        };
    }

    private async Task SelectConnectionAsync(int index)
    {
        if (index < 0 || index >= _connections.Count)
        {
            return;
        }

        _model = new DepotBrowserModel(_host, _connections[index], _http);
        _tree.ItemsSource = null;
        _detail.Children.Clear();
        _SetState("Loading projects", "Loading available Depot projects…", MaterialIconKind.DatabaseOutline);
        var answer = await _model.ProjectsAsync().ConfigureAwait(true);
        if (!_Accepted(answer))
        {
            return;
        }

        var projects = answer.Data ?? [];
        _filling = true;
        _projectBox.ItemsSource = projects;
        _projectBox.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<DepotBrowserProject>((project, _) => new TextBlock { Text = project?.Name });
        var wanted = await DefaultProjectAsync().ConfigureAwait(true);
        _projectBox.SelectedItem = projects.FirstOrDefault(project => project.Project == wanted || project.Project.EndsWith("/" + wanted, StringComparison.Ordinal)) ?? projects.FirstOrDefault();
        _filling = false;
        _project = (_projectBox.SelectedItem as DepotBrowserProject)?.Project;
        await LoadTreeAsync().ConfigureAwait(true);
    }

    // The Depot project the active Cockpit project keeps its memory in (a `depot…:<project>` reference), if any.
    private async Task<string?> DefaultProjectAsync()
    {
        var rows = await _host.GetProjectMemoryRowsAsync(_host.ActivePaneId).ConfigureAwait(true);
        var reference = rows.Select(row => row.Reference).FirstOrDefault(value => value.StartsWith(DepotPluginScheme, StringComparison.OrdinalIgnoreCase));
        return reference is null ? null : reference[(reference.IndexOf(':') + 1)..];
    }

    private const string DepotPluginScheme = "depot";

    public async Task LoadTreeAsync(string? selectPath = null)
    {
        if (_model is null || _project is null)
        {
            return;
        }

        _SetState("Loading Depot files", "Reading this project's documents and artifacts…", MaterialIconKind.FolderSearchOutline);
        var answer = await _model.TreeAsync(_project).ConfigureAwait(true);
        if (!_Accepted(answer))
        {
            return;
        }

        _loaded = answer.Data ?? new([], false);
        _upload.IsVisible = _loaded.CanShare;
        _newMarkdown.IsVisible = _loaded.CanShare;
        _publishFolder.IsVisible = _loaded.CanShare;
        if (_loaded.Nodes.Count == 0)
        {
            _SetState("No Depot files yet", "This project has no documents or artifacts yet.", MaterialIconKind.FolderOpenOutline);
        }
        else
        {
            _SetState(null, null, MaterialIconKind.FolderOpenOutline);
        }
        _detail.Children.Clear();
        _detailContainer.IsVisible = false;
        _openBadges.Clear();
        _tree.ItemsSource = DepotTreeBuilder.Build(_loaded.Nodes).Select(item => _Row(item, selectPath)).ToList();
        _RefreshOpenBadges();
        await RefreshPublicationsAsync().ConfigureAwait(true);
    }

    // AC-1574: the links from local folders to this project. A failed listing only leaves the section empty.
    private async Task RefreshPublicationsAsync()
    {
        _publications.Children.Clear();
        if (_model is null || _project is null || !_loaded.CanShare)
        {
            return;
        }

        var answer = await _model.PublicationsAsync(_project).ConfigureAwait(true);
        if (answer.Data is not { Count: > 0 } rows)
        {
            return;
        }

        _publications.Children.Add(new TextBlock { Text = "Publications", FontWeight = FontWeight.SemiBold });
        foreach (var row in rows)
        {
            var link = row.Publication;
            var last = row.Last is { } report
                ? $"Last run {report.At.ToLocalTime():yyyy-MM-dd HH:mm}: {report.Written} published, {report.Deleted} removed, {report.Problems.Count} not done."
                : "Not published yet.";
            var publish = new Button { Content = "Publish now" };
            publish.Click += async (_, _) => await PublishAsync(link).ConfigureAwait(true);
            var remove = new Button { Content = "Remove link" };
            remove.Click += async (_, _) => await RemovePublicationAsync(link).ConfigureAwait(true);
            _publications.Children.Add(new TextBlock { Text = $"{link.Source} → {(link.TargetFolder.Length == 0 ? "/" : link.TargetFolder)}", TextWrapping = TextWrapping.Wrap });
            _publications.Children.Add(_Faint(last));
            _publications.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { publish, remove } });
        }
    }

    private async Task PublishFolderAsync()
    {
        if (_model is null || _project is null || TopLevel.GetTopLevel(this) is not { } top)
        {
            return;
        }

        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions()).ConfigureAwait(true);
        if (folders.FirstOrDefault()?.TryGetLocalPath() is not { } source)
        {
            return;
        }

        if (_Accepted(await _model.AddPublicationAsync(_project, source, _SelectedFolder()).ConfigureAwait(true)))
        {
            await RefreshPublicationsAsync().ConfigureAwait(true);
        }
    }

    private async Task PublishAsync(DepotPublication link)
    {
        if (_model is not { } model
            || await model.PublishAsync(link).ConfigureAwait(true) is not { } answer
            || !_Accepted(answer)
            || answer.Data is not { } report)
        {
            return;
        }

        _host.ShowToast($"Published {report.Written}, removed {report.Deleted}, {report.Problems.Count} not done.");
        await LoadTreeAsync().ConfigureAwait(true);
    }

    private async Task RemovePublicationAsync(DepotPublication link)
    {
        if (_model is { } model && _Accepted(await model.RemovePublicationAsync(link).ConfigureAwait(true)))
        {
            await RefreshPublicationsAsync().ConfigureAwait(true);
        }
    }

    // The folder of the selected row (the root when nothing is selected).
    private string _SelectedFolder() =>
        _tree.SelectedItem is TreeViewItem { Tag: DepotTreeItem selected }
            ? (selected.Kind == DepotTreeKind.Folder ? selected.Path : selected.Path[..Math.Max(selected.Path.LastIndexOf('/'), 0)])
            : string.Empty;

    private TreeViewItem _Row(DepotTreeItem item, string? selectPath)
    {
        var header = _Header(item);
        var row = new TreeViewItem { Tag = item, Header = header, ItemsSource = item.Children.Select(child => _Row(child, selectPath)).ToList() };
        if (item.Node is not null)
        {
            var badge = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Foreground = _Brush("CockpitAccentBrush"), Margin = new Thickness(6, 0, 0, 0) };
            header.Children.Add(badge);
            _openBadges[item.Path] = badge;
            row.Tapped += async (_, _) =>
            {
                if (_PreviewMode)
                {
                    await OpenAsync(item.Node, preview: false).ConfigureAwait(true);
                }
            };
            row.DoubleTapped += async (_, _) => await OpenAsync(item.Node).ConfigureAwait(true);
        }
        _AddMarkdownMenu(row, item);
        if (selectPath is not null && (selectPath == item.Path || selectPath.StartsWith(item.Path + "/", StringComparison.Ordinal)))
        {
            row.IsExpanded = true;
            row.IsSelected = selectPath == item.Path;
        }

        return row;
    }

    private static DepotTreeItem? _Find(IEnumerable<DepotTreeItem> items, string? path)
    {
        foreach (var item in items)
        {
            if (item.Path == path)
            {
                return item;
            }

            if (_Find(item.Children, path) is { } child)
            {
                return child;
            }
        }

        return null;
    }

    private static StackPanel _Header(DepotTreeItem item)
    {
        var kind = item.Kind switch
        {
            DepotTreeKind.Folder => MaterialIconKind.FolderOutline,
            _ when item.Node is { } fileNode => _Icon(fileNode),
            _ => MaterialIconKind.FileOutline,
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(new MaterialIcon { Kind = kind, Width = 16, Height = 16 });
        row.Children.Add(new TextBlock { Text = item.Name, FontSize = 13, FontWeight = FontWeight.Normal, VerticalAlignment = VerticalAlignment.Center });
        if (item.Node is { ShareId: not null } shared)
        {
            row.Children.Add(new MaterialIcon { Kind = shared.IsPublic ? MaterialIconKind.Earth : MaterialIconKind.LinkVariant, Width = 12, Height = 12 });
        }

        if (item.Node is { } node)
        {
            row.Children.Add(_Metadata($"{_Size(node.Size)}  {node.Modified?.ToLocalTime():yyyy-MM-dd}", 11, new Thickness(6, 0, 0, 0)));
        }

        return row;
    }

    private static MaterialIconKind _Icon(DepotBrowserNode node) => node.Kind switch
    {
        DepotBrowserKind.Document => MaterialIconKind.FileDocumentOutline,
        _ when node.ContentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true => MaterialIconKind.FileImageOutline,
        _ => MaterialIconKind.FileOutline,
    };

    private async Task ShowAsync(DepotTreeItem item)
    {
        var actions = _ShowDetails(item, interactive: true);
        if (item.Node is not { } node || actions is null || _model is null || _project is null)
        {
            return;
        }

        if (node.Kind == DepotBrowserKind.Document)
        {
            if (!_PreviewMode)
            {
                var excerpt = await _model.ReadDocumentExcerptAsync(_project, node).ConfigureAwait(true);
                if (_Accepted(excerpt) && excerpt.Data is { } shown)
                {
                    _AddExcerpt(shown);
                }
            }

            var documentOpen = _Button("Open");
            documentOpen.Click += async (_, _) => await OpenAsync(node).ConfigureAwait(true);
            actions.Children.Add(documentOpen);
            if (_loaded.CanShare && DepotMarkdownFiles.IsMarkdown(node))
            {
                var edit = _Button("✎ Edit");
                edit.Click += async (_, _) => await _EditMarkdownAsync(node).ConfigureAwait(true);
                actions.Children.Add(edit);
            }
            return;
        }

        _detail.Children.Add(_ShareBlock(node));
        var open = _Button("Open");
        open.Click += async (_, _) => await OpenAsync(node).ConfigureAwait(true);
        actions.Children.Add(open);
    }

    private StackPanel? _ShowDetails(DepotTreeItem item, bool interactive)
    {
        _detail.Children.Clear();
        _detailContainer.IsVisible = false;
        if (item.Node is not { } node)
        {
            return null;
        }

        _detailContainer.IsVisible = true;
        var heading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        heading.Children.Add(new MaterialIcon { Kind = _Icon(node), Width = 16, Height = 16 });
        heading.Children.Add(new TextBlock { Text = item.Name, FontSize = 13.5, FontWeight = FontWeight.SemiBold });
        _detail.Children.Add(heading);
        _detail.Children.Add(_Metadata($"{node.Path} · {_Size(node.Size)} · {node.Modified?.ToLocalTime():yyyy-MM-dd HH:mm}", 11.5));
        var download = _Button("Download");
        if (interactive)
        {
            download.Click += async (_, _) => await DownloadAsync(node).ConfigureAwait(true);
        }

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { download } };
        _detail.Children.Add(actions);
        return actions;
    }

    private void _ShowScene(DepotTreeItem item, DepotDocumentExcerpt? excerpt)
    {
        var actions = _ShowDetails(item, interactive: false);
        if (item.Node is not { } node || actions is null)
        {
            return;
        }

        if (node.Kind == DepotBrowserKind.Document)
        {
            if (excerpt is not null)
            {
                _AddExcerpt(excerpt);
            }

            return;
        }

        _detail.Children.Add(_ShareBlock(node));
        actions.Children.Add(_Button("Open"));
    }

    private void _AddExcerpt(DepotDocumentExcerpt excerpt)
    {
        if (excerpt.Heading is null && excerpt.Paragraph is null)
        {
            return;
        }

        var text = new TextBlock
        {
            FontSize = 12.5,
            MaxLines = 3,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.Wrap,
            Foreground = _Brush("CockpitTextSecondaryBrush"),
            Inlines = [],
        };
        if (excerpt.Heading is { Length: > 0 } heading)
        {
            text.Inlines.Add(new Run(heading) { FontWeight = FontWeight.SemiBold, Foreground = _Brush("CockpitTextPrimaryBrush") });
        }

        if (excerpt.Paragraph is { Length: > 0 } paragraph)
        {
            text.Inlines.Add(new Run(excerpt.Heading is null ? paragraph : $" — {paragraph}"));
        }

        _detail.Children.Add(new Border
        {
            BorderBrush = _Brush("CockpitHairlineSoftBrush"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(0, 8, 0, 0),
            Child = text,
        });
    }

    private Control _ShareBlock(DepotBrowserNode node)
    {
        var block = new StackPanel { Spacing = 6 };
        var header = new WrapPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(new TextBlock { Text = "Share link", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 2, 8, 2) });
        header.Children.Add(_StatusChip(node.ShareId is null ? "Not shared" : node.IsPublic ? "Public link" : "Members link", node.IsPublic));
        block.Children.Add(header);
        var (members, publicLink) = _VisibilityChoices(node.IsPublic, _loaded.CanShare);
        void SelectVisibility(bool isPublic)
        {
            members.IsChecked = !isPublic;
            publicLink.IsChecked = isPublic;
        }

        if (node.ShareId is null)
        {
            block.Children.Add(_Faint("Not shared"));
            if (!_loaded.CanShare)
            {
                block.Children.Add(_Faint("Sharing needs the Editor role in this project."));
                return _ShareCard(block);
            }

            var create = _Button("Create link");
            create.Click += async (_, _) => await ShareAsync(node, publicLink.IsChecked == true, copyLink: true).ConfigureAwait(true);
            members.Click += (_, _) => SelectVisibility(isPublic: false);
            publicLink.Click += (_, _) => SelectVisibility(isPublic: true);
            block.Children.Add(_Faint("Create a link to hand this file to someone."));
            block.Children.Add(new WrapPanel { Orientation = Orientation.Horizontal, Children = { members, publicLink, create } });
            return _ShareCard(block);
        }

        if (node.ShareUrl is not { Length: > 0 } shareUrl)
        {
            block.Children.Add(_Faint("Depot did not return the share link."));
            return _ShareCard(block);
        }

        var url = new TextBox { Text = shareUrl, IsReadOnly = true, MinWidth = 180 };
        var copy = _Button("Copy");
        copy.Click += async (_, _) => await _host.SetClipboardTextAsync(shareUrl).ConfigureAwait(true);
        var openLink = _Button("Open link");
        openLink.Click += async (_, _) => await _Launch(new Uri(shareUrl)).ConfigureAwait(true);
        var linkRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        linkRow.Children.Add(url);
        Grid.SetColumn(copy, 1);
        copy.Margin = new Thickness(6, 0, 0, 0);
        linkRow.Children.Add(copy);
        Grid.SetColumn(openLink, 2);
        openLink.Margin = new Thickness(6, 0, 0, 0);
        linkRow.Children.Add(openLink);
        block.Children.Add(linkRow);
        if (_loaded.CanShare)
        {
            members.Click += async (_, _) =>
            {
                SelectVisibility(isPublic: false);
                if (node.IsPublic)
                {
                    await ShareAsync(node, isPublic: false, copyLink: false).ConfigureAwait(true);
                }
            };
            publicLink.Click += async (_, _) =>
            {
                SelectVisibility(isPublic: true);
                if (!node.IsPublic)
                {
                    await ShareAsync(node, isPublic: true, copyLink: false).ConfigureAwait(true);
                }
            };
            var stop = _Button("Stop sharing");
            stop.Click += async (_, _) => await UnshareAsync(node).ConfigureAwait(true);
            block.Children.Add(new WrapPanel { Orientation = Orientation.Horizontal, Children = { members, publicLink, stop } });
        }

        if (node.IsPublic)
        {
            block.Children.Add(_PublicWarning());
        }

        return _ShareCard(block);
    }

    private static (ToggleButton Members, ToggleButton Public) _VisibilityChoices(bool isPublic, bool isEnabled)
    {
        var members = new ToggleButton { Content = "Members only", IsChecked = !isPublic, IsEnabled = isEnabled, Margin = new Thickness(0, 0, 6, 0) };
        var publicLink = new ToggleButton { Content = "Public", IsChecked = isPublic, IsEnabled = isEnabled, Margin = new Thickness(0, 0, 6, 0) };
        members.Classes.Add("Compact");
        publicLink.Classes.Add("Compact");
        return (members, publicLink);
    }

    private static Border _StatusChip(string text, bool isPublic) => new()
    {
        Padding = new Thickness(6, 1),
        Background = _Brush(isPublic ? "CockpitStatusWaitingChipTintBrush" : "CockpitAccentRingTintBrush"),
        BorderBrush = _Brush(isPublic ? "CockpitStatusWaitingBrush" : "CockpitFocusHairlineBrush"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        Child = new TextBlock { Text = text, FontSize = 11 },
    };

    private static Border _PublicWarning() => new()
    {
        Padding = new Thickness(8),
        Background = _Brush("CockpitStatusWaitingChipTintBrush"),
        BorderBrush = _Brush("CockpitStatusWaitingBrush"),
        BorderThickness = new Thickness(1),
        CornerRadius = _Radius("CockpitControlRadius"),
        Child = new TextBlock
        {
            Text = "Anyone with this URL can open this file — no account needed. Switching back to Members only closes it again; the URL stays the same.",
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
        },
    };

    private static Border _ShareCard(Control content) => new()
    {
        Padding = new Thickness(8),
        BorderBrush = _Brush("CockpitHairlineSoftBrush"),
        BorderThickness = new Thickness(1),
        CornerRadius = _Radius("CockpitControlRadius"),
        Child = content,
    };

    private async Task ShareAsync(DepotBrowserNode node, bool isPublic, bool copyLink)
    {
        if (_model is not { } model || _project is not { } project)
        {
            return;
        }

        var answer = await model.ShareAsync(project, node, isPublic, copyLink).ConfigureAwait(true);
        if (answer is null)
        {
            // Declined: put the picker back where the file actually is.
            await LoadTreeAsync(node.Path).ConfigureAwait(true);
            return;
        }

        if (_Accepted(answer))
        {
            if (copyLink)
            {
                _host.ShowToast("Link copied to the clipboard.");
            }

            await LoadTreeAsync(node.Path).ConfigureAwait(true);
        }
    }

    private async Task UnshareAsync(DepotBrowserNode node)
    {
        if (_model is { } model && _project is { } project
            && await model.UnshareAsync(project, node).ConfigureAwait(true) is { } answer
            && _Accepted(answer))
        {
            await LoadTreeAsync(node.Path).ConfigureAwait(true);
        }
    }

    private async Task DownloadAsync(DepotBrowserNode node)
    {
        if (_model is not { } model || _project is not { } project || TopLevel.GetTopLevel(this) is not { } top)
        {
            return;
        }

        var answer = await model.DownloadAsync(project, node, async name =>
        {
            var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { SuggestedFileName = name }).ConfigureAwait(true);
            return file is null ? null : await file.OpenWriteAsync().ConfigureAwait(true);
        }).ConfigureAwait(true);
        if (_Accepted(answer) && answer.Data)
        {
            _host.ShowToast($"Saved {DepotDownload.SafeFileName(node.Path)}.");
        }
    }

    // Into the folder of the selected row (the root when nothing is selected), one file at a time.
    private async Task UploadAsync()
    {
        if (_model is null || _project is null || TopLevel.GetTopLevel(this) is not { } top)
        {
            return;
        }

        var folder = _SelectedFolder();
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = true }).ConfigureAwait(true);
        var sent = 0;
        foreach (var file in files)
        {
            var name = DepotDownload.SafeFileName(file.Name);
            var target = folder.Length == 0 ? name : $"{folder}/{name}";
            DepotBrowserAnswer<bool>? answer;
            try
            {
                await using var stream = await file.OpenReadAsync().ConfigureAwait(true);
                answer = await _model.UploadAsync(_project, folder, file.Name, stream, _loaded.Nodes.Any(node => node.Path == target)).ConfigureAwait(true);
            }
            catch (IOException exception)
            {
                _host.ShowToast($"Could not read {file.Name}: {exception.Message}");
                break;
            }

            if (answer is null)
            {
                continue;
            }

            if (!_Accepted(answer))
            {
                _host.ShowToast(answer.Message ?? "Upload failed.");
                break;
            }

            sent++;
        }

        if (sent > 0)
        {
            await LoadTreeAsync(folder.Length == 0 ? null : folder).ConfigureAwait(true);
            _host.ShowToast(sent == 1 ? "Uploaded 1 file." : $"Uploaded {sent} files.");
        }
    }

    private bool _PreviewMode => _settings?.OpenFilesInPreviewTab ?? true;

    // A single click opens the preview tab and a double-click or the Open button a kept one; a file that has no tab only opens on the latter.
    private async Task OpenAsync(DepotBrowserNode node, bool preview = false)
    {
        if (_model is { } model && _project is { } project)
        {
            if (DepotMarkdownFiles.IsMarkdown(node))
            {
                await DepotMarkdownFiles.OpenAsync(_host, model.Connection, project, node.Path, edit: false, preview: preview).ConfigureAwait(true);
                return;
            }
            if (ArtifactWorkspace.CanOpenInCockpit(node))
            {
                await _host.OpenWorkspaceAsync(ArtifactWorkspace.WorkspaceTypeId, new Cockpit.Plugins.Abstractions.Workspaces.WorkspaceOpenOptions($"depot:{model.Connection.Id}:{project}:{node.Path}", Path.GetFileName(node.Path), preview)).ConfigureAwait(true);
                return;
            }
            if (preview)
            {
                return;
            }

            _Accepted(await model.OpenAsync(project, node, _Launch).ConfigureAwait(true));
        }
    }

    private async Task SignInAsync()
    {
        if (_model is not null && await _model.SignInAsync().ConfigureAwait(true))
        {
            _signIn.IsVisible = false;
            await SelectConnectionAsync(_connectionBox.SelectedIndex).ConfigureAwait(true);
        }
    }

    private Task _Launch(Uri uri) => TopLevel.GetTopLevel(this)?.Launcher.LaunchUriAsync(uri) ?? Task.CompletedTask;

    // False (with the reason on screen) unless the answer is Ok. Not signed in gets a button and nothing else.
    private bool _Accepted<T>(DepotBrowserAnswer<T> answer)
    {
        _signIn.IsVisible = answer.Status == DepotBrowserStatus.SignIn;
        if (_signIn.IsVisible)
        {
            _signIn.Content = answer.Message;
        }

        if (answer.Status == DepotBrowserStatus.Ok)
        {
            _SetState(null, null, MaterialIconKind.FolderOpenOutline);
            return true;
        }

        _SetState(
            answer.Status == DepotBrowserStatus.SignIn ? "Sign in to Depot" : "Depot files unavailable",
            answer.Status == DepotBrowserStatus.SignIn ? "Sign in to load this project's files." : answer.Message ?? "Depot did not return a reason.",
            answer.Status == DepotBrowserStatus.SignIn ? MaterialIconKind.Login : MaterialIconKind.AlertCircleOutline);
        return false;
    }

    private void _SetState(string? title, string? message, MaterialIconKind icon)
    {
        _state.IsVisible = title is not null || message is not null || _signIn.IsVisible;
        _stateIcon.Kind = icon;
        _stateTitle.Text = title;
        _message.Text = message;
    }

    private static Button _Button(string content)
    {
        var button = new Button { Content = content };
        button.Classes.Add("Compact");
        return button;
    }

    private static TextBlock _Faint(string text) => new() { Text = text, FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = _Brush("CockpitTextSecondaryBrush") };

    private static TextBlock _Metadata(string text, double fontSize, Thickness margin = default) => new()
    {
        Text = text,
        FontSize = fontSize,
        Margin = margin,
        VerticalAlignment = VerticalAlignment.Center,
        Foreground = _Brush("CockpitTextFaintBrush"),
    };

    private static IBrush? _Brush(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is IBrush brush ? brush : null;

    private static CornerRadius _Radius(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is CornerRadius radius ? radius : default;

    private static string _Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / 1024.0 / 1024.0:0.#} MB",
    };
}
