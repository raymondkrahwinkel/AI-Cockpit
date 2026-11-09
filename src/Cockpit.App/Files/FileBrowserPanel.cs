using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Platform.Storage;
using Cockpit.Plugins.Abstractions.UI;
using Cockpit.Plugins.Abstractions.Files;
using Cockpit.Plugins.Abstractions.UI.Files;
using Material.Icons;
using Material.Icons.Avalonia;

namespace Cockpit.App.Files;

// One place a plugin's file source is reachable: this computer (Server null) or a managed server, over that plugin's channel.
internal sealed record FileBrowserRoute(string? Server, string WorkspaceTypeId, IPluginUiChannel Channel);

// AC-2236: the host's browser over any plugin's IFileSource, in the Depot browser's layout (AC-1765): toolbar, lazy tree, detail.
internal sealed class FileBrowserPanel : UserControl
{
    private sealed record Choice(FileBrowserRoute Route, FileConnection Connection)
    {
        public override string ToString() => Route.Server is null ? Connection.Label : $"{Route.Server} · {Connection.Label}";
    }

    internal sealed record Row(FileEntry Entry, FileRef Parent);

    private readonly Func<IReadOnlyList<FileBrowserRoute>> _routes;
    private readonly Func<string, FileWorkspaceOpenOptions, Task> _open;
    private readonly Action<string> _toast;
    private readonly ComboBox _connectionBox = new() { MinWidth = 200 };
    private readonly Button _newFile = _Button("New file");
    private readonly Button _newFolder = _Button("New folder");
    private readonly Button _upload = _Button("Upload");
    private readonly TextBox _name = new() { MinWidth = 180 };
    private readonly StackPanel _nameRow = new() { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(12, 0, 6, 8), IsVisible = false };
    private readonly TreeView _tree = new();
    private readonly StackPanel _detail = new() { Spacing = 6, Margin = new Thickness(12, 10) };
    private readonly Border _detailContainer = new() { IsVisible = false };
    private readonly StackPanel _state = new() { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 300 };
    private readonly MaterialIcon _stateIcon = new() { Width = 28, Height = 28, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _stateTitle = new() { FontSize = 14, FontWeight = FontWeight.SemiBold, TextAlignment = TextAlignment.Center };
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
    private readonly Dictionary<string, TreeViewItem> _folders = new(StringComparer.Ordinal);
    private Choice? _choice;
    private FileSourceChannelClient? _client;
    private Func<string, Task>? _named;
    private bool _listing;

    public FileBrowserPanel(Func<IReadOnlyList<FileBrowserRoute>> routes, Func<string, FileWorkspaceOpenOptions, Task> open, Action<string> toast)
    {
        _routes = routes;
        _open = open;
        _toast = toast;
        var refresh = _Button("Refresh");
        refresh.Click += (_, _) => _Run(LoadAsync);
        _connectionBox.SelectionChanged += (_, _) =>
        {
            if (!_listing)
            {
                _Run(_ShowConnectionAsync);
            }
        };
        _newFile.Click += (_, _) => _AskName("File name", _NewFileAsync);
        _newFolder.Click += (_, _) => _AskName("Folder name", _NewFolderAsync);
        _upload.Click += (_, _) => _Run(_UploadAsync);
        _tree.SelectionChanged += (_, _) => _ShowDetail((_tree.SelectedItem as TreeViewItem)?.Tag as Row);
        Content = _Layout(refresh);
        Ready = LoadAsync();
    }

    // Completes once the connections and the first folder are shown; the scene waits on it.
    internal Task Ready { get; }

    public async Task LoadAsync()
    {
        var previous = _choice;
        var choices = new List<Choice>();
        string? failure = null;
        foreach (var route in _routes())
        {
            try
            {
                choices.AddRange((await new FileSourceChannelClient(route.Channel).ConnectionsAsync(CancellationToken.None).ConfigureAwait(true))
                    .Select(connection => new Choice(route, connection)));
            }
            catch (FileSourceException error)
            {
                failure ??= route.Server is null ? error.Message : $"{route.Server}: {error.Message}";
            }
        }
        // Replacing the list fires SelectionChanged twice; the folder is loaded once, below.
        _listing = true;
        _connectionBox.ItemsSource = choices;
        _connectionBox.IsVisible = choices.Count > 0;
        _connectionBox.SelectedItem = choices.FirstOrDefault(choice => choice.Route.WorkspaceTypeId == previous?.Route.WorkspaceTypeId
            && choice.Connection.Id == previous.Connection.Id) ?? choices.FirstOrDefault();
        _listing = false;
        if (choices.Count == 0)
        {
            await _ShowConnectionAsync().ConfigureAwait(true);
            _SetState(failure is null ? "No file connections" : "Files unavailable",
                failure ?? "Add a connection in this plugin's settings.", failure is null ? MaterialIconKind.FolderOffOutline : MaterialIconKind.AlertCircleOutline);
        }
        else
        {
            await _ShowConnectionAsync().ConfigureAwait(true);
        }
    }

    private Control _Layout(Button refresh)
    {
        // The Depot browser's quiet selection (AC-1765), not the theme's strong accent fill.
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
        var top = new WrapPanel { Margin = new Thickness(12, 8, 6, 2) };
        foreach (var control in new Control[] { _connectionBox, refresh, _newFile, _newFolder, _upload })
        {
            control.Margin = new Thickness(0, 0, 6, 6);
            top.Children.Add(control);
        }
        var create = _Button("Create");
        create.Classes.Add("Accent");
        create.Click += (_, _) => _SubmitName();
        var cancel = _Button("Cancel");
        cancel.Classes.Add("Ghost");
        cancel.Click += (_, _) => _CancelName();
        _name.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter)
            {
                _SubmitName();
            }
            else if (e.Key == Avalonia.Input.Key.Escape)
            {
                _CancelName();
            }
        };
        _nameRow.Children.Add(_name);
        _nameRow.Children.Add(create);
        _nameRow.Children.Add(cancel);

        _message.Foreground = _Brush("CockpitTextSecondaryBrush");
        _state.Children.Add(_stateIcon);
        _state.Children.Add(_stateTitle);
        _state.Children.Add(_message);
        var treeLayer = new Grid();
        treeLayer.Children.Add(_tree);
        treeLayer.Children.Add(_state);
        var body = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        body.Children.Add(new Border { BorderBrush = _Brush("CockpitHairlineBrush"), BorderThickness = new Thickness(0, 1, 0, 0), Child = treeLayer });
        _detailContainer.Background = _Brush("CockpitPanelBgBrush");
        _detailContainer.BorderBrush = _Brush("CockpitHairlineBrush");
        _detailContainer.BorderThickness = new Thickness(0, 1, 0, 0);
        _detailContainer.MaxHeight = 260;
        _detailContainer.Child = new ScrollViewer { Content = _detail };
        Grid.SetRow(_detailContainer, 1);
        body.Children.Add(_detailContainer);

        var layout = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(_nameRow, Dock.Top);
        layout.Children.Add(top);
        layout.Children.Add(_nameRow);
        layout.Children.Add(body);
        return layout;
    }

    private async Task _ShowConnectionAsync()
    {
        _choice = _connectionBox.SelectedItem as Choice;
        _client = _choice is null ? null : new FileSourceChannelClient(_choice.Route.Channel);
        _newFile.IsVisible = _newFolder.IsVisible = _upload.IsVisible = _Writable;
        _CancelName();
        _ShowDetail(null);
        _folders.Clear();
        _tree.ItemsSource = null;
        if (_choice is null)
        {
            return;
        }
        var root = FileRef.Root(_choice.Connection.Id);
        try
        {
            var rows = await _RowsAsync(root).ConfigureAwait(true);
            _tree.ItemsSource = rows;
            _SetState(rows.Count == 0 ? "This folder is empty" : null, rows.Count == 0 ? "Add a file or a folder to start." : null, MaterialIconKind.FolderOpenOutline);
        }
        catch (FileSourceException error)
        {
            _SetState(error.Kind == FileSourceErrorKind.SignIn ? "Sign in to see these files" : "Files unavailable", error.Message,
                error.Kind == FileSourceErrorKind.SignIn ? MaterialIconKind.Login : MaterialIconKind.AlertCircleOutline);
        }
    }

    private bool _Writable => _choice is { Connection.ReadOnly: false };

    // Every page of one folder, folders first; a long folder costs one round trip per page.
    private async Task<List<TreeViewItem>> _RowsAsync(FileRef folder)
    {
        var entries = new List<FileEntry>();
        string? page = null;
        do
        {
            var listing = await _client!.ListAsync(folder, page, CancellationToken.None).ConfigureAwait(true);
            entries.AddRange(listing.Entries);
            page = listing.NextPageToken;
        }
        while (page is not null);
        return [.. entries.OrderBy(entry => entry.Kind == FileEntryKind.File).ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .Select(entry => _Row(new Row(entry, folder)))];
    }

    private TreeViewItem _Row(Row row)
    {
        var item = new TreeViewItem { Tag = row, Header = _Header(row.Entry) };
        if (row.Entry.Kind == FileEntryKind.Folder)
        {
            _folders[row.Entry.Id] = item;
            item.ItemsSource = new[] { new TreeViewItem { Header = _Faint("Loading…"), IsEnabled = false } };
            var loaded = false;
            item.PropertyChanged += (_, e) =>
            {
                if (e.Property == TreeViewItem.IsExpandedProperty && item.IsExpanded && !loaded)
                {
                    loaded = true;
                    _Run(() => _FillAsync(item, _Ref(row.Entry)));
                }
            };
        }
        else
        {
            item.Tapped += (_, _) =>
            {
                if (_IsMarkdown(row.Entry))
                {
                    _Run(() => _OpenAsync(row, edit: false, preview: true));
                }
            };
        }
        return item;
    }

    private async Task _FillAsync(TreeViewItem item, FileRef folder)
    {
        try
        {
            var rows = await _RowsAsync(folder).ConfigureAwait(true);
            item.ItemsSource = rows.Count == 0 ? new[] { new TreeViewItem { Header = _Faint("Empty folder"), IsEnabled = false } } : rows;
        }
        catch (FileSourceException error)
        {
            item.ItemsSource = new[] { new TreeViewItem { Header = _Faint(error.Message), IsEnabled = false } };
        }
    }

    private static StackPanel _Header(FileEntry entry)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var icon = new MaterialIcon { Kind = _Icon(entry), Width = 16, Height = 16 };
        if (entry.Kind == FileEntryKind.Folder)
        {
            icon.Bind(MaterialIcon.ForegroundProperty, icon.GetResourceObservable("CockpitStatusWaitingBrush"));
        }
        header.Children.Add(icon);
        header.Children.Add(new TextBlock { Text = entry.Name, FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
        if (entry.Kind == FileEntryKind.File)
        {
            header.Children.Add(_Metadata($"{_Size(entry.Size)}  {entry.Modified?.ToLocalTime():yyyy-MM-dd}", 11, new Thickness(6, 0, 0, 0)));
        }
        return header;
    }

    private void _ShowDetail(Row? row)
    {
        _detail.Children.Clear();
        _detailContainer.IsVisible = row is not null;
        if (row is null)
        {
            return;
        }
        var entry = row.Entry;
        var heading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        heading.Children.Add(new MaterialIcon { Kind = _Icon(entry), Width = 16, Height = 16 });
        heading.Children.Add(new TextBlock { Text = entry.Name, FontSize = 13.5, FontWeight = FontWeight.SemiBold });
        _detail.Children.Add(heading);
        _detail.Children.Add(_Metadata(entry.Kind == FileEntryKind.Folder ? entry.Path
            : $"{entry.Path} · {_Size(entry.Size)} · {entry.Modified?.ToLocalTime():yyyy-MM-dd HH:mm}", 11.5));
        if (entry.Kind == FileEntryKind.Folder)
        {
            return;
        }
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var download = _Button("Download");
        download.Click += (_, _) => _Run(() => _DownloadAsync(entry));
        actions.Children.Add(download);
        if (_Writable && _IsMarkdown(entry))
        {
            var edit = _Button("Edit");
            edit.Classes.Add("Accent");
            edit.Click += (_, _) => _Run(() => _OpenAsync(row, edit: true, preview: false));
            actions.Children.Add(edit);
        }
        _detail.Children.Add(actions);
    }

    private Task _OpenAsync(Row row, bool edit, bool preview, bool createNew = false)
    {
        var choice = _choice!;
        var file = _Ref(row.Entry);
        var folder = row.Parent.Path == "/" ? "" : $" / {row.Parent.Path}";
        var location = (choice.Route.Server is null ? "" : $"{choice.Route.Server} / ") + choice.Connection.Label + folder;
        return _open(choice.Route.WorkspaceTypeId, new FileWorkspaceOpenOptions(file,
            $"files:{choice.Route.WorkspaceTypeId}:{choice.Connection.Id}:{file.Id}", row.Entry.Name, location, edit, createNew,
            createNew ? $"# {Path.GetFileNameWithoutExtension(row.Entry.Name)}\n\n" : "", preview));
    }

    // The folder new items go into: the selected folder, the selected file's folder, or the top.
    private FileRef _TargetFolder() => (_tree.SelectedItem as TreeViewItem)?.Tag switch
    {
        Row { Entry.Kind: FileEntryKind.Folder } folder => _Ref(folder.Entry),
        Row file => file.Parent,
        _ => FileRef.Root(_choice!.Connection.Id),
    };

    private static FileRef _Child(FileRef folder, string name)
    {
        // A new item is addressed by its folder's Path plus its name (IFileSource.ListAsync).
        var path = folder.Path == "/" ? name : $"{folder.Path.TrimEnd('/')}/{name}";
        return new FileRef(folder.ConnectionId, path, path);
    }

    private Task _NewFileAsync(string name)
    {
        if (!_IsMarkdown(name))
        {
            name += ".md";
        }
        var folder = _TargetFolder();
        var file = _Child(folder, name);
        return _OpenAsync(new Row(new FileEntry(file.Id, name, file.Path, FileEntryKind.File), folder), edit: true, preview: false, createNew: true);
    }

    private async Task _NewFolderAsync(string name)
    {
        var folder = _TargetFolder();
        await _client!.CreateFolderAsync(folder, name, CancellationToken.None).ConfigureAwait(true);
        await _ReloadAsync(folder).ConfigureAwait(true);
    }

    private async Task _UploadAsync()
    {
        if (TopLevel.GetTopLevel(this) is not { } top || _client is null)
        {
            return;
        }
        var folder = _TargetFolder();
        var picked = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = false }).ConfigureAwait(true);
        if (picked.Count == 0)
        {
            return;
        }
        var name = picked[0].Name;
        byte[] bytes;
        try
        {
            await using var stream = await picked[0].OpenReadAsync().ConfigureAwait(true);
            bytes = await FileSourceChannel.ReadContentAsync(stream, FileSourceChannel.MaxContentBytes, CancellationToken.None).ConfigureAwait(true);
        }
        catch (FileSourceException error) when (error.Kind == FileSourceErrorKind.TooLarge)
        {
            _toast($"{name} is larger than 1 MiB, the upload limit for now.");
            return;
        }
        using var content = new MemoryStream(bytes, writable: false);
        var result = await _client.WriteAsync(_Child(folder, name), content, null, mustNotExist: true, CancellationToken.None).ConfigureAwait(true);
        if (result.Status == FileWriteStatus.Conflict)
        {
            _toast($"{name} already exists in this folder.");
            return;
        }
        await _ReloadAsync(folder).ConfigureAwait(true);
        _toast($"Uploaded {name}.");
    }

    // In 1 MiB blocks; a version change between blocks would mix two files, so it stops and removes the partial copy.
    private async Task _DownloadAsync(FileEntry entry)
    {
        if (TopLevel.GetTopLevel(this) is not { } top || _client is null)
        {
            return;
        }
        var target = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { SuggestedFileName = entry.Name }).ConfigureAwait(true);
        if (target is null)
        {
            return;
        }
        var file = _Ref(entry);
        try
        {
            await using var output = await target.OpenWriteAsync().ConfigureAwait(true);
            string? version = null;
            for (long offset = 0; ;)
            {
                var (bytes, blockVersion, size) = await _client.ReadRangeAsync(file, offset, FileSourceChannel.MaxContentBytes, CancellationToken.None).ConfigureAwait(true);
                if ((version ??= blockVersion) != blockVersion || (bytes.Length == 0 && offset < size))
                {
                    throw new FileSourceException(FileSourceErrorKind.Unavailable, $"{entry.Name} changed during the download. Try again.");
                }
                await output.WriteAsync(bytes).ConfigureAwait(true);
                offset += bytes.Length;
                if (offset >= size)
                {
                    break;
                }
            }
        }
        catch
        {
            await target.DeleteAsync().ConfigureAwait(true);
            throw;
        }
        _toast($"Saved {entry.Name}.");
    }

    private async Task _ReloadAsync(FileRef folder)
    {
        if (folder.Path == "/")
        {
            await _ShowConnectionAsync().ConfigureAwait(true);
        }
        else if (_folders.TryGetValue(folder.Id, out var item))
        {
            await _FillAsync(item, folder).ConfigureAwait(true);
            item.IsExpanded = true;
        }
    }

    private void _AskName(string watermark, Func<string, Task> then)
    {
        _named = then;
        _name.Text = "";
        _name.PlaceholderText = watermark;
        _nameRow.IsVisible = true;
        _name.Focus();
    }

    private void _SubmitName()
    {
        var name = _name.Text?.Trim() ?? "";
        if (_named is not { } then || name.Length == 0)
        {
            return;
        }
        if (name is "." or ".." || name.IndexOfAny(['/', '\\']) >= 0)
        {
            _toast("A name cannot contain a slash.");
            return;
        }
        _CancelName();
        _Run(() => then(name));
    }

    private void _CancelName()
    {
        _named = null;
        _nameRow.IsVisible = false;
    }

    // Click handlers cannot await; the guarded task says a failure in a toast and the browser stays as it was.
    private void _Run(Func<Task> action) => _ = _GuardAsync(action);

    private async Task _GuardAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (FileSourceException error)
        {
            _toast(error.Message);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            _toast($"The file action failed: {error.Message}");
        }
    }

    private FileRef _Ref(FileEntry entry) => new(_choice!.Connection.Id, entry.Id, entry.Path);

    private static bool _IsMarkdown(FileEntry entry) => entry.Kind == FileEntryKind.File && _IsMarkdown(entry.Name);

    private static bool _IsMarkdown(string name) =>
        name.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase);

    private static MaterialIconKind _Icon(FileEntry entry) => entry.Kind == FileEntryKind.Folder ? MaterialIconKind.Folder
        : _IsMarkdown(entry) ? MaterialIconKind.FileDocumentOutline : MaterialIconKind.FileOutline;

    private void _SetState(string? title, string? message, MaterialIconKind icon)
    {
        _state.IsVisible = title is not null || message is not null;
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

    private static TextBlock _Faint(string text) => new() { Text = text, FontSize = 11, Foreground = _Brush("CockpitTextSecondaryBrush") };

    private static TextBlock _Metadata(string text, double fontSize, Thickness margin = default) => new()
    {
        Text = text,
        FontSize = fontSize,
        Margin = margin,
        VerticalAlignment = VerticalAlignment.Center,
        TextWrapping = TextWrapping.Wrap,
        Foreground = _Brush("CockpitTextFaintBrush"),
    };

    private static IBrush? _Brush(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is IBrush brush ? brush : null;

    private static string _Size(long? bytes) => bytes switch
    {
        null => "",
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / 1024.0 / 1024.0:0.#} MB",
    };
}
