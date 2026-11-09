using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Cockpit.App.Controls;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Sessions;
using Material.Icons;

namespace Cockpit.App.Views;

internal sealed class CompanionStatusPanel : UserControl
{
    private readonly CompanionAttentionFeed _feed;
    private Window? _mainWindow;
    private readonly StackPanel _body = new();

    public CompanionStatusPanel(CompanionAttentionFeed feed)
    {
        _feed = feed;
        Content = _body;
        _Refresh();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _mainWindow = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (_mainWindow is not null)
        {
            _mainWindow.PropertyChanged += _OnMainWindowChanged;
        }
        _feed.StatusChanged += _Refresh;
        _Refresh();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_mainWindow is not null)
        {
            _mainWindow.PropertyChanged -= _OnMainWindowChanged;
        }
        _feed.StatusChanged -= _Refresh;
        base.OnDetachedFromVisualTree(e);
    }

    private void _OnMainWindowChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
    {
        if (args.Property == Visual.IsVisibleProperty || args.Property == Window.WindowStateProperty)
        {
            _Refresh();
        }
    }

    // AC-2190: the companion-strook mockup's status pop-out — the Cockpit mark with where the main window is and Open
    // Cockpit in the head, then one two-line row per agent (name, what it is doing) with the selected one marked, then
    // the services as name and value.
    private void _Refresh()
    {
        _body.Children.Clear();
        var main = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        var where = main is { IsVisible: true }
            ? main.WindowState == WindowState.Minimized ? "Main window minimized" : "Main window open"
            : "Main window in the tray";
        var mark = new Image { Source = CockpitWindowChrome.BrandMark, Width = 20, Height = 20, VerticalAlignment = VerticalAlignment.Center };
        var title = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        title.Children.Add(CompanionPanelChrome.Text("Cockpit", weight: FontWeight.SemiBold));
        title.Children.Add(CompanionPanelChrome.Text(where, "CockpitTextSecondaryBrush", 11));
        var brand = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { mark, title } };
        var actions = new List<Control>();
        if (main is not { IsVisible: true })
        {
            var open = new Button { Content = "Open Cockpit", Classes = { "Accent", "Compact" }, VerticalAlignment = VerticalAlignment.Center };
            open.Click += (_, _) => (Application.Current as App)?.ShowMainWindow();
            actions.Add(open);
        }
        _body.Children.Add(CompanionPanelChrome.Header(brand, CompanionPanelChrome.CloseOf(this), [.. actions]));

        var rows = new StackPanel { Margin = new Thickness(6, 0, 6, 8) };
        rows.Children.Add(_Label("Agents"));
        foreach (var session in _feed.StatusSessions)
        {
            using var card = new AgentOverviewCardViewModel(session, _feed.Cockpit);
            var brush = session.SessionStatus is SessionStatus.Busy or SessionStatus.WorkingBackground
                && !_feed.Cockpit.Sessions.Contains(session) ? "CockpitStatusBackgroundBrush" : session.SessionStatusBrushKey;
            var selected = ReferenceEquals(_feed.Cockpit.SelectedSession, session);
            var dot = new Ellipse { Width = 7, Height = 7, VerticalAlignment = VerticalAlignment.Center };
            dot.Bind(Shape.FillProperty, dot.GetResourceObservable(brush));
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(CompanionPanelChrome.Text(card.Title, weight: FontWeight.SemiBold));
            text.Children.Add(CompanionPanelChrome.Text(session.Statusline.Length > 0 ? session.Statusline : session.SessionStatusLabel, "CockpitTextSecondaryBrush", 11));
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 9 };
            row.Children.Add(dot);
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            if (selected)
            {
                var tag = CompanionPanelChrome.Text("Selected", "CockpitTextSecondaryBrush", 11.5);
                tag.VerticalAlignment = VerticalAlignment.Center;
                tag.TextWrapping = TextWrapping.NoWrap;
                Grid.SetColumn(tag, 2);
                row.Children.Add(tag);
            }
            var button = new Button
            {
                Content = row,
                Tag = session,
                Classes = { "Ghost", "companionPickRow" },
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(8, 6),
                CornerRadius = new CornerRadius(7),
            };
            button.Classes.Set("selected", selected);
            AutomationProperties.SetName(button, selected ? $"{card.Title}, selected" : $"Select {card.Title}");
            button.Click += (_, _) => _feed.Cockpit.SelectSessionCommand.Execute(session);
            rows.Children.Add(button);
        }
        rows.Children.Add(_Label("Services"));
        foreach (var status in _feed.Cockpit.PluginStatusTexts)
        {
            rows.Children.Add(_StatusRow(MaterialIconKind.CloudCheckOutline, status.PluginId, status.Text));
        }
        rows.Children.Add(_StatusRow(MaterialIconKind.MicrophoneOutline, "Audio",
            _feed.Cockpit.IsAudioAvailable ? "Available" : _feed.Cockpit.AudioUnavailableMessage));
        _body.Children.Add(rows);
    }

    private static Grid _StatusRow(MaterialIconKind icon, string name, string status)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 9, Margin = new Thickness(8, 6) };
        var glyph = CockpitIcons.Icon(icon, 15);
        glyph.Bind(Material.Icons.Avalonia.MaterialIcon.ForegroundProperty, glyph.GetResourceObservable("CockpitTextSecondaryBrush"));
        row.Children.Add(glyph);
        var title = CompanionPanelChrome.Text(name);
        Grid.SetColumn(title, 1);
        row.Children.Add(title);
        var value = CompanionPanelChrome.Text(status, "CockpitTextSecondaryBrush", 11.5);
        value.HorizontalAlignment = HorizontalAlignment.Right;
        value.TextAlignment = TextAlignment.Right;
        value.MaxWidth = 170;
        value.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(value, 2);
        row.Children.Add(value);
        return row;
    }

    private static TextBlock _Label(string text)
    {
        var label = CompanionPanelChrome.Label(text);
        label.Margin = new Thickness(8, 4, 8, 4);
        return label;
    }
}
