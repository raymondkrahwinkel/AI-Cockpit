using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Cockpit.Plugin.Depot.Contracts;
using Cockpit.Plugins.Abstractions.UI;
using Material.Icons;
using Material.Icons.Avalonia;

namespace Cockpit.Plugin.Depot.UI;

internal sealed class DepotNotesPanel : UserControl
{
    private readonly ICockpitUiHost _host;
    private readonly DepotNotesModel _model;
    private readonly DepotInboxModel _inbox;
    private readonly IReadOnlyList<DepotConnectionRegistration> _connections;
    private readonly ComboBox _connectionBox = new() { MinWidth = 100 };
    private readonly ComboBox _projectBox = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly StackPanel _body = new();
    private readonly TextBlock _status = _Text("", 11, "CockpitStatusDoneBrush", mono: true);
    private readonly Button _new = new() { HorizontalAlignment = HorizontalAlignment.Stretch, Classes = { "Accent" } };
    private DepotConnectionRegistration? _connection;
    private IReadOnlyList<DepotBrowserNode> _recent = [];
    private bool _filling;
    private bool _loading;
    private int _contextVersion;
    private bool _attached;

    public DepotNotesPanel(ICockpitUiHost host, DepotNotesModel model, DepotInboxModel inbox,
        IReadOnlyList<DepotConnectionRegistration> connections, Action<string> openInbox)
    {
        _host = host;
        _model = model;
        _inbox = inbox;
        _connections = connections;
        FontSize = 12.5;
        var pencil = new MaterialIcon { Kind = MaterialIconKind.Pencil, Width = 14, Height = 14 };
        _Bind(pencil, MaterialIcon.ForegroundProperty, "CockpitTextOnAccentBrush");
        _new.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center,
            Children = { pencil, _Text("New meeting note", 12.5, "CockpitTextOnAccentBrush", FontWeight.SemiBold) },
        };
        _new.Click += async (_, _) => await _RunAsync(async () =>
        {
            if (_connection is { } connection)
            {
                await _model.NewAsync(connection, _Project, _Launch).ConfigureAwait(true);
            }
        }).ConfigureAwait(true);
        var refresh = new Button { Classes = { "Compact" }, Content = new MaterialIcon { Kind = MaterialIconKind.Refresh, Width = 14, Height = 14 } };
        ToolTip.SetTip(refresh, "Refresh");
        refresh.Click += async (_, _) => await _RunAsync(_RefreshAsync).ConfigureAwait(true);
        _connectionBox.ItemsSource = connections.Select(connection => connection.Name).ToList();
        _connectionBox.IsVisible = connections.Count > 1;
        _connectionBox.SelectionChanged += async (_, _) => await _RunAsync(_SelectConnectionAsync).ConfigureAwait(true);
        _projectBox.ItemTemplate = new FuncDataTemplate<DepotBrowserProject>((project, _) => new StackPanel
        {
            Spacing = 2,
            Children =
            {
                _Text(project?.Name ?? string.Empty, 12.5, weight: FontWeight.SemiBold),
                _Text($"{project?.Project.Split('/').FirstOrDefault()} · {(_connection is { } selected ? new Uri(selected.Url).Host : string.Empty)}", 11, "CockpitTextFaintBrush"),
            },
        });
        _projectBox.SelectionChanged += async (_, _) =>
        {
            if (!_filling)
            {
                await _RunAsync(_RefreshAsync).ConfigureAwait(true);
            }
        };
        var header = new Grid { ColumnDefinitions = new("*,Auto"), Margin = new(12, 8), ColumnSpacing = 8 };
        var selectors = new StackPanel { Spacing = 6, Children = { _connectionBox, _projectBox } };
        header.Children.Add(selectors);
        Grid.SetColumn(refresh, 1);
        header.Children.Add(refresh);
        var footer = new Grid { ColumnDefinitions = new("*,Auto"), Margin = new(12, 6) };
        footer.Children.Add(_Text("Only notes you may open", 11, "CockpitTextFaintBrush", mono: true));
        Grid.SetColumn(_status, 1);
        footer.Children.Add(_status);
        var layout = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        var create = new Border { Padding = new(12, 10, 12, 0), Child = _new };
        DockPanel.SetDock(create, Dock.Top);
        Header = header;
        layout.Children.Add(footer);
        layout.Children.Add(create);
        layout.Children.Add(new ScrollViewer { Content = _body, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        Content = layout;
        _OpenInbox = openInbox;
        AttachedToVisualTree += (_, _) =>
        {
            if (!_attached)
            {
                _model.Changed += _OnChanged;
                _attached = true;
                if (_connection is null && connections.Count > 0)
                {
                    _connectionBox.SelectedIndex = 0;
                }
                else
                {
                    _Render();
                }
            }
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _model.Changed -= _OnChanged;
            _attached = false;
        };
        if (connections.Count == 0)
        {
            _Render();
        }
    }

    private readonly Action<string> _OpenInbox;
    public Control Header { get; }
    private string? _Project => (_projectBox.SelectedItem as DepotBrowserProject)?.Project;

    private async Task _SelectConnectionAsync()
    {
        if (_connectionBox.SelectedIndex < 0 || _connectionBox.SelectedIndex >= _connections.Count)
        {
            return;
        }

        _connection = _connections[_connectionBox.SelectedIndex];
        _recent = [];
        var version = ++_contextVersion;
        var connection = _connection;
        var projects = await _InvokeAsync<DepotBrowserAnswer<IReadOnlyList<DepotBrowserProject>>>(DepotBrowserChannel.Projects, new DepotBrowserRequest(connection.Id)).ConfigureAwait(true);
        if (version != _contextVersion)
        {
            return;
        }

        _filling = true;
        _projectBox.ItemsSource = projects?.Data ?? [];
        var rows = await _host.GetProjectMemoryRowsAsync(_host.ActivePaneId).ConfigureAwait(true);
        var wanted = rows.Select(row => row.Reference).FirstOrDefault(reference => reference.StartsWith("depot", StringComparison.OrdinalIgnoreCase));
        var address = wanted is null ? null : wanted[(wanted.IndexOf(':') + 1)..];
        _projectBox.SelectedItem = projects?.Data?.FirstOrDefault(project => project.Project == address) ?? projects?.Data?.FirstOrDefault();
        _filling = false;
        await _RefreshAsync().ConfigureAwait(true);
    }

    private async Task _RefreshAsync()
    {
        if (_connection is not { } connection)
        {
            return;
        }

        _loading = true;
        try
        {
            await _model.ListAsync(connection.Id).ConfigureAwait(true);
            await _inbox.ListAsync(connection.Id).ConfigureAwait(true);
            await _LoadRecentAsync().ConfigureAwait(true);
        }
        finally
        {
            _loading = false;
            _Render();
        }
    }

    private async Task _LoadRecentAsync()
    {
        _recent = [];
        if (_connection is not { } connection || _Project is not { } project
            || _model.PageOf(connection.Id)?.State != DepotInboxState.Ok)
        {
            return;
        }

        var version = ++_contextVersion;
        var answer = await _InvokeAsync<DepotBrowserAnswer<DepotBrowserTree>>(DepotBrowserChannel.Tree, new DepotBrowserRequest(connection.Id, project)).ConfigureAwait(true);
        if (version == _contextVersion && _connection?.Id == connection.Id && _Project == project
            && _model.PageOf(connection.Id)?.State == DepotInboxState.Ok)
        {
            _recent = (answer?.Data?.Nodes ?? []).Where(node => node.Kind == DepotBrowserKind.Document
                    && node.Path.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(node => node.Modified).Take(14).ToList();
        }
    }

    private void _OnChanged(DepotNotesPage page) => Dispatcher.UIThread.Post(async () =>
    {
        if (page.ConnectionId == _connection?.Id)
        {
            _Render();
            if (!_loading)
            {
                await _RunAsync(async () =>
                {
                    await _LoadRecentAsync().ConfigureAwait(true);
                    _Render();
                }).ConfigureAwait(true);
            }
        }
    });

    private void _Render()
    {
        _body.Children.Clear();
        var page = _connection is { } connection ? _model.PageOf(connection.Id) : null;
        _new.IsEnabled = _connection is not null;
        _status.Text = page?.State switch
        {
            DepotInboxState.Ok => "● connected",
            DepotInboxState.Unreachable => "● unreachable",
            _ => "● not signed in",
        };
        _Bind(_status, TextBlock.ForegroundProperty, page?.State == DepotInboxState.Ok ? "CockpitStatusDoneBrush" : "CockpitTextSecondaryBrush");
        if (page?.State is not DepotInboxState.Ok)
        {
            _recent = [];
            var text = _Text(_connection is null ? "Add a Depot server in the Depot settings first."
                : page?.State == DepotInboxState.Unreachable ? "Depot is unreachable. Retrying…" : "Sign in to Depot to load notes.", 12.5, "CockpitTextSecondaryBrush");
            text.Margin = new(12, 16);
            _body.Children.Add(text);
            if (_connection is { } signedOut && page?.State is DepotInboxState.SignIn or DepotInboxState.Expired)
            {
                var signIn = new Button { Content = "Sign in to Depot", Classes = { "Compact" }, Margin = new(12, 0) };
                signIn.Click += async (_, _) => await _RunAsync(async () =>
                {
                    if (await _inbox.SignInAsync(signedOut).ConfigureAwait(true))
                    {
                        await _SelectConnectionAsync().ConfigureAwait(true);
                    }
                }).ConfigureAwait(true);
                _body.Children.Add(signIn);
            }
            return;
        }

        _body.Children.Add(_Section("● LIVE NOW", page.Notes.Count.ToString()));
        foreach (var note in page.Notes)
        {
            _body.Children.Add(_Card(page.ConnectionId, note));
        }
        if (page.Notes.Count == 0)
        {
            _body.Children.Add(new Border { Padding = new(12, 4), Child = _Text("No live notes.", 12.5, "CockpitTextSecondaryBrush") });
        }
        var inbox = new Button { Content = $"All in Inbox ({_model.InboxCount(page.ConnectionId)})", Classes = { "Ghost", "Compact" }, Margin = new(12, 0), HorizontalAlignment = HorizontalAlignment.Left };
        inbox.Click += (_, _) => _OpenInbox(page.ConnectionId);
        _body.Children.Add(inbox);
        _body.Children.Add(_Section("RECENT NOTES"));
        foreach (var folder in _recent.GroupBy(node => Path.GetDirectoryName(node.Path)?.Replace('\\', '/') ?? string.Empty))
        {
            if (folder.Key.Length > 0)
            {
                var folderRow = new Grid { ColumnDefinitions = new("Auto,*,Auto"), ColumnSpacing = 7, Margin = new(16, 4, 12, 4) };
                var folderIcon = new MaterialIcon { Kind = MaterialIconKind.Folder, Width = 14, Height = 14 };
                _Bind(folderIcon, MaterialIcon.ForegroundProperty, "CockpitStatusWaitingBrush");
                folderRow.Children.Add(folderIcon);
                var folderName = _Text(folder.Key, 12.5);
                Grid.SetColumn(folderName, 1);
                folderRow.Children.Add(folderName);
                var count = _Text(folder.Count().ToString(), 11, "CockpitTextFaintBrush", mono: true);
                Grid.SetColumn(count, 2);
                folderRow.Children.Add(count);
                _body.Children.Add(folderRow);
            }
            foreach (var node in folder)
            {
                var live = page.Notes.FirstOrDefault(note => note.Address == _Project && note.Path == node.Path);
                var row = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 6 };
                row.Children.Add(_Text("✎ " + Path.GetFileName(node.Path), 12.5));
                if (live is not null)
                {
                    var badge = new Border { Padding = new(6, 0), CornerRadius = _Radius("CockpitPillRadius"), Child = _Text($"● live · {live.People.Count}", 10.5, "CockpitStatusDoneBrush", FontWeight.SemiBold, mono: true) };
                    _Bind(badge, Border.BackgroundProperty, "CockpitStatusDoneChipTintBrush");
                    Grid.SetColumn(badge, 1);
                    row.Children.Add(badge);
                }
                else
                {
                    var size = _Text(node.Size < 1024 ? $"{node.Size} B" : $"{node.Size / 1024.0:0.#} KB", 11, "CockpitTextFaintBrush", mono: true);
                    Grid.SetColumn(size, 1);
                    row.Children.Add(size);
                }
                var open = new Button { Content = row, FontWeight = FontWeight.Normal, Classes = { "Ghost", "Compact" }, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Margin = new(16, 0, 8, 0) };
                open.Click += async (_, _) => await _RunAsync(async () =>
                {
                    if (_connection is { } selected && _Project is { } project)
                    {
                        if (live is not null)
                        {
                            await _model.JoinAsync(selected, live, _Launch).ConfigureAwait(true);
                        }
                        else
                        {
                            await DepotMarkdownFiles.OpenAsync(_host, selected, project, node.Path, edit: false).ConfigureAwait(true);
                        }
                    }
                }).ConfigureAwait(true);
                _body.Children.Add(open);
            }
        }
        if (_recent.Count == 0)
        {
            _body.Children.Add(new Border { Padding = new(12, 4), Child = _Text("No recent notes.", 12.5, "CockpitTextSecondaryBrush") });
        }
    }

    private Control _Card(string connectionId, DepotLiveNote note)
    {
        var title = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8 };
        title.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 7,
            Children = { _Text("●", 12.5, "CockpitStatusDoneBrush"), _Text(note.Title, 12.5, weight: FontWeight.SemiBold) },
        });
        var people = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        foreach (var person in note.People.Take(4))
        {
            var letter = _Text(person.Name.Length > 0 ? person.Name[..1].ToUpperInvariant() : "?", 10, "CockpitTextSecondaryBrush", FontWeight.SemiBold);
            letter.HorizontalAlignment = HorizontalAlignment.Center;
            letter.VerticalAlignment = VerticalAlignment.Center;
            var avatar = new Border { Width = 22, Height = 22, BorderThickness = new(1), CornerRadius = _Radius("CockpitPillRadius"), Child = letter };
            _Bind(avatar, Border.BorderBrushProperty, "CockpitTextSecondaryBrush");
            if (person.Color is { Length: 7 } color && color[0] == '#' && Color.TryParse(color, out var parsed))
            {
                avatar.BorderBrush = new SolidColorBrush(parsed);
                letter.Foreground = avatar.BorderBrush;
            }
            ToolTip.SetTip(avatar, person.Name);
            people.Children.Add(avatar);
        }
        Grid.SetColumn(people, 1);
        title.Children.Add(people);
        var content = new StackPanel { Spacing = 4, Children = { title, _Text(note.Path, 11.5, "CockpitTextSecondaryBrush", mono: true) } };
        if (_model.InviterOf(connectionId, note) is { } inviter)
        {
            content.Children.Add(_Text($"Invited by {inviter}", 11.5, "CockpitTextSecondaryBrush"));
        }
        var actions = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8 };
        var names = _Text(string.Join(", ", note.People.Select(person => person.Name)), 11.5, "CockpitTextSecondaryBrush");
        names.VerticalAlignment = VerticalAlignment.Center;
        actions.Children.Add(names);
        var join = new Button { Content = note.Join ? "Join" : "Watch", Classes = { "Compact" } };
        if (note.Join)
        {
            join.Classes.Add("Accent");
        }
        Grid.SetColumn(join, 1);
        join.Click += async (_, _) => await _RunAsync(async () =>
        {
            if (_connection is { } connection)
            {
                await _model.JoinAsync(connection, note, _Launch).ConfigureAwait(true);
            }
        }).ConfigureAwait(true);
        actions.Children.Add(join);
        content.Children.Add(actions);
        var card = new Border { Margin = new(12, 0, 12, 8), Padding = new(10, 9), BorderThickness = new(1), CornerRadius = _Radius("CockpitControlRadius"), Child = content };
        _Bind(card, Border.BackgroundProperty, "CockpitPanelBgBrush");
        _Bind(card, Border.BorderBrushProperty, "CockpitStatusDoneRingTintBrush");
        return card;
    }

    private static Control _Section(string label, string? count = null)
    {
        var row = new Grid { ColumnDefinitions = new("*,Auto"), Margin = new(12, 12, 12, 6) };
        row.Children.Add(_Text(label, 11, "CockpitTextSecondaryBrush", FontWeight.SemiBold));
        if (count is not null)
        {
            var number = _Text(count, 11, "CockpitTextSecondaryBrush");
            Grid.SetColumn(number, 1);
            row.Children.Add(number);
        }
        return row;
    }

    private static TextBlock _Text(string text, double size, string brush = "CockpitTextPrimaryBrush", FontWeight? weight = null, bool mono = false)
    {
        var block = new TextBlock { Text = text, FontSize = size, FontWeight = weight ?? FontWeight.Normal, TextWrapping = TextWrapping.Wrap };
        _Bind(block, TextBlock.ForegroundProperty, brush);
        if (mono)
        {
            _Bind(block, TextBlock.FontFamilyProperty, "CockpitMonoFont");
        }
        return block;
    }

    private static CornerRadius _Radius(string key) => Application.Current?.TryFindResource(key, out var value) == true && value is CornerRadius radius ? radius : default;

    private static void _Bind(Control control, AvaloniaProperty property, string key) =>
        control.Bind(property, control.GetResourceObservable(key));

    private Task _Launch(Uri uri) => TopLevel.GetTopLevel(this)?.Launcher.LaunchUriAsync(uri) ?? Task.CompletedTask;

    private async Task<T?> _InvokeAsync<T>(string action, object request)
    {
        var answer = await _host.Channel.InvokeAsync(action, JsonSerializer.SerializeToElement(request, DepotChannel.Json)).ConfigureAwait(true);
        return answer.Deserialize<T>(DepotChannel.Json);
    }

    private async Task _RunAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            Trace.TraceWarning($"Depot Notes action failed: {exception}");
            _host.ShowToast("Could not load Depot notes. Try Refresh.");
        }
    }
}
