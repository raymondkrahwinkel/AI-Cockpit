using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Cockpit.Plugin.Depot.Contracts;
using Cockpit.Plugins.Abstractions.UI;
using Material.Icons;
using Material.Icons.Avalonia;

namespace Cockpit.Plugin.Depot.UI;

// AC-1568: browse a connected Depot project — documents and artifacts in one tree, view or download either, and for
// an artifact manage its share link. The behaviour is DepotBrowserModel's; this is only the controls.
internal sealed class DepotBrowserPanel : UserControl
{
    private readonly ICockpitUiHost _host;
    private readonly IReadOnlyList<DepotConnectionRegistration> _connections;
    private readonly HttpClient _http = new();
    private readonly ComboBox _connectionBox = new() { MinWidth = 140 };
    private readonly ComboBox _projectBox = new() { MinWidth = 200 };
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _signIn = new() { IsVisible = false };
    private readonly Button _upload = new() { Content = "Upload", IsVisible = false };
    private readonly Button _publishFolder = new() { Content = "Publish folder here…", IsVisible = false };
    private readonly StackPanel _publications = new() { Spacing = 6, Margin = new Thickness(12, 6) };
    private readonly TreeView _tree = new();
    private readonly StackPanel _detail = new() { Spacing = 8, Margin = new Thickness(12) };
    private DepotBrowserModel? _model;
    private DepotBrowserTree _loaded = new([], false);
    private string? _project;
    private bool _filling;

    // AC-1586: `inbox` is the plugin's shared inbox model (the badge's), or null for a panel that keeps its own;
    // `openInbox` starts on the Inbox tab, as the side-menu button does.
    public DepotBrowserPanel(ICockpitUiHost host, IReadOnlyList<DepotConnectionRegistration> connections, DepotInboxModel? inbox = null, bool openInbox = false)
    {
        _host = host;
        _connections = connections;
        var refresh = new Button { Content = "Refresh" };
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
        var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(12, 8), Children = { _connectionBox, _projectBox, refresh, _upload, _publishFolder } };
        var banner = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(12, 0, 12, 6), Children = { _message, _signIn } };
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("360,*") };
        body.Children.Add(new Border { BorderBrush = _Brush("CockpitHairlineBrush"), BorderThickness = new Thickness(0, 1, 1, 0), Child = _tree });
        var detail = new ScrollViewer { Content = _detail };
        Grid.SetColumn(detail, 1);
        body.Children.Add(detail);
        var layout = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(banner, Dock.Top);
        DockPanel.SetDock(_publications, Dock.Bottom);
        layout.Children.Add(top);
        layout.Children.Add(banner);
        layout.Children.Add(_publications);
        layout.Children.Add(body);
        var ownsInbox = inbox is null;
        var inboxModel = inbox ?? new DepotInboxModel(host);
        if (ownsInbox)
        {
            DetachedFromVisualTree += (_, _) => inboxModel.Dispose();
        }

        var inboxTab = new TabItem { Header = "Inbox", Content = new DepotInboxPanel(inboxModel, connections) };
        Content = new TabControl
        {
            Items = { new TabItem { Header = "Files", Content = layout }, inboxTab },
            SelectedItem = openInbox ? inboxTab : null,
        };

        if (connections.Count == 0)
        {
            _message.Text = "Add a Depot server in the Depot settings first.";
            return;
        }

        _connectionBox.SelectedIndex = 0;
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
        _message.Text = "Loading projects…";
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

        _message.Text = "Loading…";
        var answer = await _model.TreeAsync(_project).ConfigureAwait(true);
        if (!_Accepted(answer))
        {
            return;
        }

        _loaded = answer.Data ?? new([], false);
        _upload.IsVisible = _loaded.CanShare;
        _publishFolder.IsVisible = _loaded.CanShare;
        _message.Text = _loaded.Nodes.Count == 0 ? "This project has no documents or artifacts yet." : string.Empty;
        _tree.ItemsSource = DepotTreeBuilder.Build(_loaded.Nodes).Select(item => _Row(item, selectPath)).ToList();
        _detail.Children.Clear();
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
        if (await _model!.PublishAsync(link).ConfigureAwait(true) is not { } answer || !_Accepted(answer))
        {
            return;
        }

        var report = answer.Data!;
        _host.ShowToast($"Published {report.Written}, removed {report.Deleted}, {report.Problems.Count} not done.");
        await LoadTreeAsync().ConfigureAwait(true);
    }

    private async Task RemovePublicationAsync(DepotPublication link)
    {
        if (_Accepted(await _model!.RemovePublicationAsync(link).ConfigureAwait(true)))
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
        var row = new TreeViewItem { Tag = item, Header = _Header(item), ItemsSource = item.Children.Select(child => _Row(child, selectPath)).ToList() };
        if (selectPath is not null && (selectPath == item.Path || selectPath.StartsWith(item.Path + "/", StringComparison.Ordinal)))
        {
            row.IsExpanded = true;
            row.IsSelected = selectPath == item.Path;
        }

        return row;
    }

    private static Control _Header(DepotTreeItem item)
    {
        var kind = item.Kind switch
        {
            DepotTreeKind.Folder => MaterialIconKind.FolderOutline,
            DepotTreeKind.Document => MaterialIconKind.FileDocumentOutline,
            _ when item.Node?.ContentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true => MaterialIconKind.FileImageOutline,
            _ => MaterialIconKind.FileOutline,
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(new MaterialIcon { Kind = kind, Width = 16, Height = 16 });
        row.Children.Add(new TextBlock { Text = item.Name, VerticalAlignment = VerticalAlignment.Center });
        if (item.Node is { ShareId: not null } shared)
        {
            row.Children.Add(new MaterialIcon { Kind = shared.IsPublic ? MaterialIconKind.Earth : MaterialIconKind.LinkVariant, Width = 12, Height = 12 });
        }

        if (item.Node is { } node)
        {
            row.Children.Add(_Faint($"{_Size(node.Size)}  {node.Modified?.ToLocalTime():yyyy-MM-dd}"));
        }

        return row;
    }

    private async Task ShowAsync(DepotTreeItem item)
    {
        _detail.Children.Clear();
        if (item.Node is not { } node || _model is null || _project is null)
        {
            return;
        }

        _detail.Children.Add(new TextBlock { Text = item.Name, FontSize = 16, FontWeight = FontWeight.SemiBold });
        _detail.Children.Add(_Faint($"{node.Path} · {_Size(node.Size)} · {node.Modified?.ToLocalTime():yyyy-MM-dd HH:mm}"));
        var download = new Button { Content = "Download" };
        download.Click += async (_, _) => await DownloadAsync(node).ConfigureAwait(true);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { download } };
        _detail.Children.Add(actions);

        if (node.Kind == DepotBrowserKind.Document)
        {
            var view = await _model.RenderDocumentAsync(_project, node).ConfigureAwait(true);
            if (_Accepted(view) && view.Data is { } markdown)
            {
                _detail.Children.Add(markdown);
            }

            return;
        }

        _detail.Children.Add(_ShareBlock(node));
        if (DepotBrowserModel.CanPreview(node))
        {
            var preview = await _model.PreviewAsync(_project, node).ConfigureAwait(true);
            if (_Accepted(preview) && preview.Data is { } shown)
            {
                _detail.Children.Add(shown.Image is { } image
                    ? new Image { Source = image, MaxHeight = 480, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left }
                    : new SelectableTextBlock { Text = shown.Text, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas, Menlo, monospace") });
            }

            return;
        }

        var open = new Button { Content = "Open" };
        open.Click += async (_, _) => await OpenAsync(node).ConfigureAwait(true);
        actions.Children.Add(open);
    }

    private Control _ShareBlock(DepotBrowserNode node)
    {
        var block = new StackPanel { Spacing = 6 };
        block.Children.Add(new TextBlock { Text = "Share link", FontWeight = FontWeight.SemiBold });
        var visibility = new ComboBox { ItemsSource = new[] { "Members only", "Public" }, SelectedIndex = node.IsPublic ? 1 : 0, IsEnabled = _loaded.CanShare };
        if (node.ShareId is null)
        {
            block.Children.Add(_Faint("Not shared"));
            if (!_loaded.CanShare)
            {
                block.Children.Add(_Faint("Sharing needs the Editor role in this project."));
                return block;
            }

            var create = new Button { Content = "Create link" };
            create.Click += async (_, _) => await ShareAsync(node, visibility.SelectedIndex == 1, copyLink: true).ConfigureAwait(true);
            block.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { visibility, create } });
            return block;
        }

        block.Children.Add(new TextBlock { Text = node.IsPublic ? "Public link" : "Members link", Foreground = _Brush(node.IsPublic ? "CockpitStatusWaitingBrush" : "CockpitAccentBrush") });
        if (node.IsPublic)
        {
            block.Children.Add(_Faint("Anyone with this URL can open the file, no account needed."));
        }

        if (node.ShareUrl is not { Length: > 0 } shareUrl)
        {
            block.Children.Add(_Faint("Depot did not return the share link."));
            return block;
        }

        block.Children.Add(new TextBox { Text = shareUrl, IsReadOnly = true });
        var copy = new Button { Content = "Copy" };
        copy.Click += async (_, _) => await _host.SetClipboardTextAsync(shareUrl).ConfigureAwait(true);
        var openLink = new Button { Content = "Open link" };
        openLink.Click += async (_, _) => await _Launch(new Uri(shareUrl)).ConfigureAwait(true);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { copy, openLink } };
        if (_loaded.CanShare)
        {
            var wasPublic = node.IsPublic;
            visibility.SelectionChanged += async (_, _) =>
            {
                if ((visibility.SelectedIndex == 1) != wasPublic)
                {
                    await ShareAsync(node, visibility.SelectedIndex == 1, copyLink: false).ConfigureAwait(true);
                }
            };
            var stop = new Button { Content = "Stop sharing" };
            stop.Click += async (_, _) => await UnshareAsync(node).ConfigureAwait(true);
            row.Children.Insert(0, visibility);
            row.Children.Add(stop);
        }

        block.Children.Add(row);
        return block;
    }

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
        var answer = await _model!.DownloadAsync(_project!, node, async name =>
        {
            var file = await TopLevel.GetTopLevel(this)!.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { SuggestedFileName = name }).ConfigureAwait(true);
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

    private async Task OpenAsync(DepotBrowserNode node) =>
        _Accepted(await _model!.OpenAsync(_project!, node, _Launch).ConfigureAwait(true));

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
            _message.Text = string.Empty;
            return true;
        }

        _message.Text = answer.Status == DepotBrowserStatus.SignIn ? "Not signed in." : answer.Message;
        return false;
    }

    private static TextBlock _Faint(string text) => new() { Text = text, FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = _Brush("CockpitTextSecondaryBrush") };

    private static IBrush? _Brush(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is IBrush brush ? brush : null;

    private static string _Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / 1024.0 / 1024.0:0.#} MB",
    };
}
