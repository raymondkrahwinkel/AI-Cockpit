using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cockpit.Plugin.Depot.Contracts;

namespace Cockpit.Plugin.Depot.UI;

// AC-1586: the Inbox tab of the Depot panel (mockup 7b–11c). One connection's inbox at a time, never merged with
// another's; the list is what the backend last fetched from Depot, and a push re-renders it through the model.
internal sealed class DepotInboxPanel : UserControl
{
    private readonly DepotInboxModel _model;
    private readonly IReadOnlyList<DepotConnectionRegistration> _connections;
    private readonly ComboBox _connectionBox = new() { MinWidth = 140 };
    private readonly Border _banner = new() { Padding = new Thickness(12, 8), IsVisible = false };
    private readonly WrapPanel _chips = new() { Margin = new Thickness(12, 8, 12, 4) };
    private readonly StackPanel _list = new() { Spacing = 2, Margin = new Thickness(8, 0, 8, 8) };
    private readonly TextBlock _footerLeft = _Mono(string.Empty);
    private readonly TextBlock _footerRight = _Mono(string.Empty);
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);
    private DepotConnectionRegistration? _connection;
    private string? _organization;

    public DepotInboxPanel(DepotInboxModel model, IReadOnlyList<DepotConnectionRegistration> connections)
    {
        _model = model;
        _connections = connections;
        var refresh = new Button { Content = "Refresh" };
        refresh.Click += async (_, _) => await LoadAsync().ConfigureAwait(true);
        _connectionBox.ItemsSource = connections.Select(connection => connection.Name).ToList();
        _connectionBox.IsVisible = false; // AC-2010: the panel header above the tabs picks the Depot.
        _connectionBox.SelectionChanged += async (_, _) =>
        {
            if (_connectionBox.SelectedIndex >= 0 && _connectionBox.SelectedIndex < _connections.Count)
            {
                _connection = _connections[_connectionBox.SelectedIndex];
                _organization = null;
                _expanded.Clear();
                await LoadAsync().ConfigureAwait(true);
            }
        };

        var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(12, 8), Children = { _connectionBox, refresh } };
        var footer = new DockPanel { Margin = new Thickness(12, 6), Children = { _footerRight, _footerLeft } };
        DockPanel.SetDock(_footerRight, Dock.Right);
        var layout = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(_banner, Dock.Top);
        DockPanel.SetDock(_chips, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        layout.Children.Add(top);
        layout.Children.Add(_banner);
        layout.Children.Add(_chips);
        layout.Children.Add(new Border { BorderBrush = _Brush("CockpitHairlineBrush"), BorderThickness = new Thickness(0, 1, 0, 0), Child = footer, [DockPanel.DockProperty] = Dock.Bottom });
        layout.Children.Add(new ScrollViewer { Content = _list });
        Content = layout;

        _model.Changed += _OnChanged;
        DetachedFromVisualTree += (_, _) => _model.Changed -= _OnChanged;
        if (connections.Count == 0)
        {
            _ShowEmpty("Add a Depot server in the Depot settings first.");
            return;
        }

        _connectionBox.SelectedIndex = 0;
    }

    public async Task LoadAsync()
    {
        if (_connection is not { } connection)
        {
            return;
        }

        if (_model.PageOf(connection.Id) is { } known)
        {
            _Render(known);
        }

        if (await _model.ListAsync(connection.Id).ConfigureAwait(true) is { } page)
        {
            _Render(page);
        }
    }

    // For the screenshot scenes: the card of this message open, as a click on it leaves it.
    internal void Expand(string messageId)
    {
        _expanded.Add(messageId);
        if (_connection is { } connection && _model.PageOf(connection.Id) is { } page)
        {
            _Render(page);
        }
    }

    public void SelectConnection(string connectionId)
    {
        var index = _connections.ToList().FindIndex(connection => connection.Id == connectionId);
        if (index >= 0)
        {
            _connectionBox.SelectedIndex = index;
        }
    }

    // Only this connection's pages; another connection's push changes the badge, not this list.
    private void _OnChanged(DepotInboxPage page) => Dispatcher.UIThread.Post(() =>
    {
        if (page.Count.ConnectionId == _connection?.Id)
        {
            _Render(page);
        }
    });

    private void _Render(DepotInboxPage page)
    {
        if (_connection is not { } connection)
        {
            return;
        }

        var state = page.Count.State;
        _RenderBanner(connection, page);
        _chips.Children.Clear();
        _list.Children.Clear();
        _footerLeft.Text = state switch
        {
            DepotInboxState.Unreachable => page.UpdatedAt is { } at ? $"Last updated {at.ToLocalTime():HH:mm}" : "Not loaded yet",
            DepotInboxState.Ok => "Only messages for this account",
            _ => connection.Name,
        };
        (_footerRight.Text, _footerRight.Foreground) = state switch
        {
            DepotInboxState.Ok => ("● connected", _Brush("CockpitStatusDoneBrush")),
            DepotInboxState.Unreachable => ("● unreachable", _Brush("CockpitStatusWaitingBrush")),
            DepotInboxState.Expired => ("● sign-in expired", _Brush("CockpitStatusErrorBrush")),
            _ => ("● not signed in", _Brush("CockpitTextSecondaryBrush")),
        };

        // 10a and 10b: no list and no titles at all, only the way back in.
        if (state is DepotInboxState.SignIn or DepotInboxState.Expired)
        {
            _list.Children.Add(_SignInPrompt(connection, state));
            return;
        }

        _RenderChips(page);
        var messages = page.Messages.Where(message => _organization is null || message.Organization?.Slug == _organization).ToList();
        if (messages.Count == 0)
        {
            _ShowEmpty(page.Messages.Count == 0 ? "No messages." : "No messages from this organization.");
            return;
        }

        foreach (var message in messages)
        {
            _list.Children.Add(_Row(message, reachable: state == DepotInboxState.Ok));
        }
    }

    private void _RenderBanner(DepotConnectionRegistration connection, DepotInboxPage page)
    {
        var (text, brush) = page.Count.State switch
        {
            DepotInboxState.Expired => ($"Your Depot sign-in has expired. Sign in again to {connection.Name}. The list from before is not kept on screen: whoever signs in next may be someone else.", "CockpitStatusErrorBrush"),
            DepotInboxState.Unreachable => (page.UpdatedAt is { } at
                ? $"Depot is unreachable. This is the list from {at.ToLocalTime():HH:mm}; opening waits until Depot is back. Retrying…"
                : "Depot is unreachable. Retrying…", "CockpitStatusWaitingBrush"),
            _ => (null, null),
        };
        _banner.IsVisible = text is not null;
        if (text is not null && brush is not null)
        {
            _banner.Background = _Brush(brush) is ISolidColorBrush solid ? new SolidColorBrush(solid.Color, 0.12) : null;
            _banner.Child = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = _Brush(brush) };
        }
    }

    private void _RenderChips(DepotInboxPage page)
    {
        var names = page.Messages.Where(message => message.Organization is not null)
            .GroupBy(message => message.Organization?.Slug ?? string.Empty)
            .ToDictionary(group => group.Key, group => group.First().Organization?.Name ?? group.Key, StringComparer.Ordinal);
        _chips.Children.Add(_Chip($"All · {page.Count.Unread}", null));
        foreach (var (slug, unread) in page.Count.PerOrganization.OrderByDescending(pair => pair.Value))
        {
            _chips.Children.Add(_Chip($"{names.GetValueOrDefault(slug, slug)} · {unread}", slug));
        }
    }

    private ToggleButton _Chip(string label, string? organization)
    {
        var chip = new ToggleButton
        {
            Content = label,
            IsChecked = _organization == organization,
            Padding = new Thickness(10, 2),
            Margin = new Thickness(0, 0, 6, 4),
            CornerRadius = new CornerRadius(10),
            FontSize = 12,
        };
        chip.Click += (_, _) =>
        {
            _organization = organization;
            if (_connection is { } connection && _model.PageOf(connection.Id) is { } page)
            {
                _Render(page);
            }
        };
        return chip;
    }

    private Control _Row(DepotInboxMessage message, bool reachable)
    {
        var expanded = _expanded.Contains(message.Id);
        var dot = new Border
        {
            Width = 6,
            Height = 6,
            CornerRadius = new CornerRadius(3),
            Background = message.IsRead ? Brushes.Transparent : _Brush("CockpitAccentBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        var kind = new Border
        {
            BorderBrush = _Brush(message.Kind == DepotInboxKind.NoteInvitation ? "CockpitAccentBrush" : "CockpitStatusWaitingBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(4, 0),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = _Mono(_KindLabel(message.Kind)),
        };
        var summary = new TextBlock
        {
            Text = message.Summary,
            FontWeight = message.IsRead ? FontWeight.Normal : FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var time = _Mono(_When(message.CreatedAt));
        var head = new DockPanel { Children = { time, dot, kind, summary } };
        DockPanel.SetDock(time, Dock.Right);
        DockPanel.SetDock(dot, Dock.Left);
        DockPanel.SetDock(kind, Dock.Left);

        var body = new StackPanel { Spacing = 6, Margin = new Thickness(14, 0, 0, 0) };
        body.Children.Add(_Where(message));
        if (expanded)
        {
            _Details(message, body);
            body.Children.Add(_Actions(message, reachable));
        }

        var row = new Border
        {
            Padding = new Thickness(8, 8),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(expanded ? 1 : 0),
            BorderBrush = _Brush("CockpitHairlineBrush"),
            Background = expanded ? _Brush("CockpitSecondaryBgBrush") : Brushes.Transparent,
            Opacity = reachable ? 1 : 0.55,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new StackPanel { Spacing = 4, Children = { head, body } },
        };
        row.PointerPressed += (_, args) =>
        {
            if (args.Source is Visual source && source.FindAncestorOfType<Button>(includeSelf: true) is not null)
            {
                return;
            }

            if (!_expanded.Remove(message.Id))
            {
                _expanded.Add(message.Id);
            }

            if (_connection is { } connection && _model.PageOf(connection.Id) is { } page)
            {
                _Render(page);
            }
        };
        return row;
    }

    // Organization and, for a note, whether someone is in it. Only what Depot said; a missing organization stays missing.
    private Control _Where(DepotInboxMessage message)
    {
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        if (message.Organization is { } organization)
        {
            line.Children.Add(_Faint(organization.Name));
        }

        if (message.Note is { State: "note" } note)
        {
            line.Children.Add(note.Live
                ? new TextBlock { Text = "· ● live now", FontSize = 12, Foreground = _Brush("CockpitStatusDoneBrush") }
                : _Faint("· not live"));
        }

        return line;
    }

    private static void _Details(DepotInboxMessage message, StackPanel body)
    {
        if (message.Note is not { } note)
        {
            if (message.Kind != DepotInboxKind.NoteInvitation)
            {
                body.Children.Add(_Faint("Granting a role, transfers and the like stay in Depot: they change who may do what."));
            }

            return;
        }

        // The title and path only where Depot gave them for this account now (DEP-403); never filled in from before.
        if (note.Title is { } title)
        {
            body.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        }

        if (note.Path is { } path)
        {
            body.Children.Add(_Mono(path));
        }

        var why = note.State switch
        {
            "noaccess" => "You have no access to this note any more.",
            "waiting" => "Waiting until you get access to the project.",
            "gone" => "This note is gone.",
            _ => null,
        };
        if (why is not null)
        {
            body.Children.Add(_Faint(why));
        }
    }

    private Control _Actions(DepotInboxMessage message, bool reachable)
    {
        var (label, _) = DepotInboxModel.PrimaryAction(message);
        var open = new Button { Content = label, Classes = { "accent" }, IsEnabled = reachable };
        open.Click += async (_, _) => await DepotInboxModel.OpenAsync(message, _LaunchAsync).ConfigureAwait(true);
        var actions = new WrapPanel { Children = { open } };
        if (_connection is { } owner && message.Kind == DepotInboxKind.NoteInvitation && message.Note?.State == "note" && _model.Join is { } join)
        {
            // AC-1581: Join opens the note in Cockpit; once it has, the card says so and brings the tab back (mockup 8b).
            actions.Children.Clear();
            if (_model.ShowTabOf(owner.Id, message.Id) is { } show)
            {
                var showTab = new Button { Content = "Show tab", IsEnabled = reachable };
                showTab.Click += async (_, _) => await _GuardedAsync(show).ConfigureAwait(true);
                actions.Children.Add(_JoinedPill());
                actions.Children.Add(showTab);
            }
            else
            {
                var joinButton = new Button { Content = "Join", Classes = { "Accent" }, IsEnabled = reachable };
                joinButton.Click += async (_, _) => await _GuardedAsync(() => join(owner, message)).ConfigureAwait(true);
                open.Classes.Remove("accent");
                actions.Children.Add(joinButton);
                actions.Children.Add(open);
            }
        }

        if (!message.IsRead)
        {
            var markRead = new Button { Content = "Mark read", Margin = new Thickness(8, 0, 0, 0), IsEnabled = reachable };
            markRead.Click += async (_, _) =>
            {
                if (_connection is { } connection && await _model.MarkReadAsync(connection.Id, message.Id).ConfigureAwait(true) is { } page)
                {
                    _Render(page);
                }
            };
            actions.Children.Add(markRead);
        }

        return actions;
    }

    // A click handler has nobody to catch for it: a route that throws is logged, and the card stays as it was.
    private static async Task _GuardedAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            System.Diagnostics.Trace.TraceWarning($"Depot: a note action in the inbox failed: {exception.Message}");
        }
    }

    private static Control _JoinedPill()
    {
        var pill = new Border
        {
            Padding = new Thickness(8, 1),
            Margin = new Thickness(0, 0, 8, 0),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = "● Joined", FontSize = 11, Foreground = _Brush("CockpitStatusDoneBrush") },
        };
        pill.Background = _Brush("CockpitStatusDoneChipTintBrush");
        pill.BorderBrush = _Brush("CockpitStatusDoneRingTintBrush");
        return pill;
    }

    private Control _SignInPrompt(DepotConnectionRegistration connection, string state)
    {
        var expired = state == DepotInboxState.Expired;
        var signIn = new Button { Content = expired ? "Sign in again" : $"Sign in to {connection.Name}", Classes = { "accent" }, HorizontalAlignment = HorizontalAlignment.Center };
        signIn.Click += async (_, _) =>
        {
            if (await _model.SignInAsync(connection).ConfigureAwait(true))
            {
                await LoadAsync().ConfigureAwait(true);
            }
        };
        return new StackPanel
        {
            Spacing = 10,
            Margin = new Thickness(24, 48),
            HorizontalAlignment = HorizontalAlignment.Center,
            Children =
            {
                new TextBlock
                {
                    Text = expired ? "Inbox hidden until you sign in again" : "Sign in to see this Depot's inbox",
                    FontWeight = FontWeight.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                },
                new TextBlock
                {
                    Text = expired
                        ? "The list from before is not kept on screen: whoever signs in next may be someone else."
                        : "Nothing of the inbox is shown until you do.",
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Center,
                    MaxWidth = 320,
                    Foreground = _Brush("CockpitTextSecondaryBrush"),
                },
                signIn,
            },
        };
    }

    private void _ShowEmpty(string text) =>
        _list.Children.Add(new TextBlock { Text = text, Margin = new Thickness(8, 16), Foreground = _Brush("CockpitTextSecondaryBrush") });

    private Task _LaunchAsync(Uri uri) => TopLevel.GetTopLevel(this)?.Launcher.LaunchUriAsync(uri) ?? Task.CompletedTask;

    private static string _KindLabel(string kind) => kind switch
    {
        DepotInboxKind.NoteInvitation => "note",
        _ when kind.Contains("Access", StringComparison.Ordinal) => "access",
        _ when kind.Contains("Invit", StringComparison.Ordinal) => "invite",
        _ => kind.ToLowerInvariant(),
    };

    private static string _When(DateTimeOffset at)
    {
        var local = at.ToLocalTime();
        var now = DateTimeOffset.Now;
        return (now - local) switch
        {
            { TotalMinutes: < 1 } => "just now",
            _ when local.Date == now.Date => local.ToString("HH:mm"),
            _ when local.Date == now.Date.AddDays(-1) => "yesterday",
            _ => local.ToString("yyyy-MM-dd"),
        };
    }

    private static TextBlock _Faint(string text) =>
        new() { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = _Brush("CockpitTextSecondaryBrush") };

    private static TextBlock _Mono(string text) =>
        new() { Text = text, FontSize = 11, FontFamily = new FontFamily("Consolas, Menlo, monospace"), Foreground = _Brush("CockpitTextSecondaryBrush"), VerticalAlignment = VerticalAlignment.Center };

    private static IBrush? _Brush(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is IBrush brush ? brush : null;
}
