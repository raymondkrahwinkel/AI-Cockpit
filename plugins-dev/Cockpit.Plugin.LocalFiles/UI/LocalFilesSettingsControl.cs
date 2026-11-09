using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Cockpit.Plugin.LocalFiles.Contracts;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Projects;
using Cockpit.Plugins.Abstractions.UI;
using Material.Icons;
using Material.Icons.Avalonia;

namespace Cockpit.Plugin.LocalFiles.UI;

// Board 1 of AC-1785: the added folders, read only until switched, and project folders as shortcuts. Every path is
// checked by the backend where the plugin runs, so the same view serves the desktop and a server's admin connection.
internal sealed class LocalFilesSettingsControl : UserControl, IAsyncPluginSettingsView
{
    private readonly IPluginUiChannel _channel;
    private readonly Func<Task<IReadOnlyList<PluginProjectInfo>>> _projects;
    private readonly List<LocalRoot> _roots = [];
    private readonly Dictionary<string, string> _hidden = [];
    private readonly StackPanel _rows = new() { Spacing = 6 };
    private readonly StackPanel _projectRows = new();
    private readonly Border _banner;
    private readonly ContentControl _refusal = new();
    private readonly StackPanel _picker = new() { Spacing = 6, IsVisible = false };
    private IReadOnlyList<PluginProjectInfo> _projectList = [];
    private bool _remote;

    public LocalFilesSettingsControl(IPluginUiChannel channel, Func<Task<IReadOnlyList<PluginProjectInfo>>> projects)
    {
        _channel = channel;
        _projects = projects;
        Styles.Add(new Style(x => x.OfType<Button>().Class("rowAction")) { Setters = { new Setter(OpacityProperty, 0.0) } });
        foreach (var shown in new[] { ":pointerover", ":focus-within" })
        {
            Styles.Add(new Style(x => x.OfType<Border>().Class("folderRow").Class(shown).Descendant().OfType<Button>().Class("rowAction"))
            {
                Setters = { new Setter(OpacityProperty, 1.0) },
            });
        }
        var add = new Button { Content = "+ Add folder…", Margin = new Thickness(0, 4, 0, 0) };
        add.Click += async (_, _) => await AddFolderAsync().ConfigureAwait(true);
        _banner = _Banner();
        Content = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                _banner,
                _Section("FOLDERS"),
                _Hint("Only the folders listed here appear in the Files browser. A new folder is read only; turn on writing per folder."),
                _rows,
                _refusal,
                _picker,
                add,
                _Section("COCKPIT PROJECT FOLDERS"),
                _Hint("Shortcuts to the folders of your Cockpit projects. Nothing is added until you click Add."),
                _projectRows,
            },
        };
        Ready = LoadAsync();
    }

    internal Task Ready { get; }

    public bool TryStage(out Func<Task>? commit, out string? error)
    {
        var roots = _roots.ToArray();
        commit = async () =>
        {
            var answer = await _InvokeAsync<LocalRootsAnswer>(LocalFilesChannel.Save, new LocalSaveRequest(roots)).ConfigureAwait(true);
            if (answer?.Error is { } refused)
            {
                throw new InvalidOperationException(refused);
            }
        };
        error = null;
        return true;
    }

    private async Task LoadAsync()
    {
        try
        {
            var answer = await _InvokeAsync<LocalRootsAnswer>(LocalFilesChannel.Roots, new LocalPathRequest(null)).ConfigureAwait(true);
            _roots.AddRange(answer?.Roots ?? []);
            foreach (var root in _roots)
            {
                if (answer?.Hidden.TryGetValue(root.Id, out var hidden) == true)
                {
                    _hidden[root.Path] = hidden;
                }
            }
            _remote = answer?.Machine != Environment.MachineName;
            _projectList = await _projects().ConfigureAwait(true);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _Refuse("Folders not loaded", "The plugin could not be asked for its folders. Close this page and try again.");
        }
        _banner.IsVisible = _remote;
        _Render();
    }

    private async Task AddFolderAsync()
    {
        if (_remote)
        {
            await _BrowseAsync(null).ConfigureAwait(true);
            return;
        }
        var picked = TopLevel.GetTopLevel(this) is { } top
            ? await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Select folder" }).ConfigureAwait(true)
            : [];
        if (picked.Count > 0 && picked[0].TryGetLocalPath() is { } path)
        {
            await AddAsync(path, project: false).ConfigureAwait(true);
        }
    }

    internal async Task AddAsync(string path, bool project)
    {
        _refusal.Content = null;
        var check = await _InvokeAsync<LocalRootCheck>(LocalFilesChannel.Check, new LocalPathRequest(path)).ConfigureAwait(true);
        if (check?.Path is not { } real)
        {
            _Refuse("Folder not added", check?.Error ?? "The plugin gave no answer. Try again.");
            return;
        }
        if (!_roots.Any(root => root.Path == real))
        {
            _roots.Add(new LocalRoot(string.Empty, real, Writable: false, project));
            if (check.Hidden is { } hidden)
            {
                _hidden[real] = hidden;
            }
        }
        _Render();
    }

    // The server's folders, listed by the plugin there: the native picker only sees this computer's disk.
    private async Task _BrowseAsync(string? path)
    {
        var folders = await _InvokeAsync<LocalFolders>(LocalFilesChannel.Folders, new LocalPathRequest(path)).ConfigureAwait(true);
        var list = new ListBox { MaxHeight = 220, ItemsSource = folders?.Children ?? [] };
        list.DoubleTapped += async (_, _) =>
        {
            if (list.SelectedItem is string child)
            {
                await _BrowseAsync(child).ConfigureAwait(true);
            }
        };
        var up = new Button { Content = "Up", Classes = { "Compact" }, IsEnabled = folders?.Path is not null };
        up.Click += async (_, _) => await _BrowseAsync(Path.GetDirectoryName(folders?.Path ?? "")).ConfigureAwait(true);
        var cancel = new Button { Content = "Cancel", Classes = { "Ghost" } };
        cancel.Click += (_, _) => _picker.IsVisible = false;
        var select = new Button { Content = "Select folder", Classes = { "Accent" } };
        select.Click += async (_, _) =>
        {
            if ((list.SelectedItem as string ?? folders?.Path) is { } chosen)
            {
                _picker.IsVisible = false;
                await AddAsync(chosen, project: false).ConfigureAwait(true);
            }
        };
        _picker.Children.Clear();
        _picker.Children.Add(_Section("CHOOSE A FOLDER ON THE SERVER"));
        _picker.Children.Add(_Hint(folders?.Error ?? "Folders on the server's disk. Double-click a folder to open it."));
        _picker.Children.Add(_Mono(folders?.Path ?? "Drives"));
        _picker.Children.Add(list);
        _picker.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { up, cancel, select } });
        _picker.IsVisible = true;
    }

    private void _Render()
    {
        _rows.Children.Clear();
        if (_roots.Count == 0)
        {
            _rows.Children.Add(_Hint("No folders yet. The Files browser shows nothing until you add one."));
        }
        foreach (var root in _roots.ToList())
        {
            _rows.Children.Add(_Row(root));
        }
        _projectRows.Children.Clear();
        foreach (var project in _projectList.Where(project => !string.IsNullOrWhiteSpace(project.SourceDirectory)))
        {
            var added = _roots.Any(root => root.Project && string.Equals(root.Path, project.SourceDirectory, StringComparison.OrdinalIgnoreCase));
            var button = new Button { Content = added ? "Added" : "Add", Classes = { "Compact" }, IsEnabled = !added };
            button.Click += async (_, _) => await AddAsync(project.SourceDirectory!, project: true).ConfigureAwait(true);
            var row = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
            DockPanel.SetDock(button, Dock.Right);
            row.Children.Add(button);
            row.Children.Add(new StackPanel { Children = { new TextBlock { Text = project.Name }, _Mono(project.SourceDirectory!) } });
            _projectRows.Children.Add(row);
        }
    }

    private Border _Row(LocalRoot root)
    {
        var toggle = new ToggleSwitch { IsChecked = root.Writable, OnContent = "Read and write", OffContent = "Read only", VerticalAlignment = VerticalAlignment.Center };
        toggle.IsCheckedChanged += (_, _) => _roots[_roots.IndexOf(root)] = root = root with { Writable = toggle.IsChecked == true };
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(root.Path)) is { Length: > 0 } leaf ? leaf : root.Path;
        var remove = new Button { Classes = { "Subtle", "rowAction" }, Content = _Icon(MaterialIconKind.Close), VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(remove, "Remove folder");
        AutomationProperties.SetName(remove, $"Remove {name}");
        remove.Click += (_, _) =>
        {
            _roots.Remove(root);
            _Render();
        };
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { new TextBlock { Text = name, FontWeight = FontWeight.SemiBold } } };
        if (root.Project)
        {
            title.Children.Add(_Pill("project"));
        }
        var text = new StackPanel { Children = { title, _Mono(root.Path) } };
        if (_hidden.TryGetValue(root.Path, out var hidden))
        {
            text.Children.Add(_Hint($"Cockpit's own state folder inside it ({hidden}) stays hidden and refused."));
        }
        var icon = _Icon(MaterialIconKind.Folder);
        icon.Bind(MaterialIcon.ForegroundProperty, icon.GetResourceObservable("CockpitStatusWaitingBrush"));
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), ColumnSpacing = 10 };
        grid.Children.Add(icon);
        grid.Children.Add(text);
        grid.Children.Add(toggle);
        grid.Children.Add(remove);
        for (var column = 0; column < grid.Children.Count; column++)
        {
            Grid.SetColumn(grid.Children[column], column);
        }
        var row = new Border { Classes = { "folderRow" }, Padding = new Thickness(12, 8), BorderThickness = new Thickness(1), Child = grid };
        _Themed(row);
        return row;
    }

    private void _Refuse(string title, string reason)
    {
        var icon = _Icon(MaterialIconKind.AlertCircleOutline);
        icon.Classes.Add("errorIcon");
        _refusal.Content = new Border
        {
            Classes = { "errorCard", "blocking" },
            Padding = new Thickness(12, 10),
            Margin = new Thickness(0, 4, 0, 0),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                Children = { icon, new StackPanel { Children = { new TextBlock { Text = title, FontWeight = FontWeight.SemiBold }, _Hint(reason) } } },
            },
        };
    }

    private async Task<T?> _InvokeAsync<T>(string action, object request) =>
        (await _channel.InvokeAsync(action, JsonSerializer.SerializeToElement(request, LocalFilesChannel.Json)).ConfigureAwait(true))
            .Deserialize<T>(LocalFilesChannel.Json);

    private static Border _Banner()
    {
        var icon = _Icon(MaterialIconKind.InformationOutline);
        icon.Bind(MaterialIcon.ForegroundProperty, icon.GetResourceObservable("CockpitAccentBrush"));
        var banner = new Border
        {
            IsVisible = false,
            Padding = new Thickness(12, 10),
            Margin = new Thickness(0, 0, 0, 6),
            Child = new DockPanel
            {
                Children =
                {
                    icon,
                    new StackPanel
                    {
                        Margin = new Thickness(10, 0, 0, 0),
                        Spacing = 2,
                        Children =
                        {
                            new TextBlock { Text = "These are folders on the server's disk, not on this computer.", FontWeight = FontWeight.SemiBold },
                            _Hint("Only an admin adds them. Everyone who administers this server can then browse them from their own desktop. Cockpit's own state folder is always refused, also inside a folder you add."),
                        },
                    },
                },
            },
        };
        banner.Bind(Border.BackgroundProperty, banner.GetResourceObservable("CockpitCategoryTintBlueBrush"));
        banner.Bind(Border.CornerRadiusProperty, banner.GetResourceObservable("CockpitControlRadius"));
        return banner;
    }

    private static void _Themed(Border border)
    {
        border.Bind(Border.BackgroundProperty, border.GetResourceObservable("CockpitSecondaryBgBrush"));
        border.Bind(Border.BorderBrushProperty, border.GetResourceObservable("CockpitHairlineBrush"));
        border.Bind(Border.CornerRadiusProperty, border.GetResourceObservable("CockpitControlRadius"));
    }

    private static Border _Pill(string text)
    {
        var pill = new Border { Padding = new Thickness(8, 0), BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center, Child = new TextBlock { Text = text, FontSize = 11 } };
        pill.Bind(Border.BorderBrushProperty, pill.GetResourceObservable("CockpitHairlineBrush"));
        pill.Bind(Border.CornerRadiusProperty, pill.GetResourceObservable("CockpitPillRadius"));
        return pill;
    }

    private static MaterialIcon _Icon(MaterialIconKind kind) => new() { Kind = kind, Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center };

    private static TextBlock _Section(string text) => new() { Text = text, Classes = { "optionsSectionHeader" } };

    private static TextBlock _Hint(string text) => new() { Text = text, Classes = { "optionsHint" } };

    private static TextBlock _Mono(string text)
    {
        var mono = new TextBlock { Text = text, FontSize = 11.5, TextWrapping = TextWrapping.Wrap };
        mono.Bind(TextBlock.FontFamilyProperty, mono.GetResourceObservable("CockpitMonoFont"));
        mono.Bind(TextBlock.ForegroundProperty, mono.GetResourceObservable("CockpitTextFaintBrush"));
        return mono;
    }
}
