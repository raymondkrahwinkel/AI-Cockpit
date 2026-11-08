using Avalonia;
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
    private readonly StackPanel _body = new() { Spacing = 8, Margin = new Thickness(14) };

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

    private void _Refresh()
    {
        _body.Children.Clear();
        _body.Children.Add(new TextBlock { Text = "Cockpit", FontWeight = FontWeight.SemiBold });
        var main = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        _body.Children.Add(_StatusRow(MaterialIconKind.Monitor, "Main window", main is { IsVisible: true }
            ? main.WindowState == WindowState.Minimized ? "Minimized" : "Open" : "In tray"));
        _body.Children.Add(_Heading("Agents"));
        foreach (var session in _feed.StatusSessions)
        {
            using var card = new AgentOverviewCardViewModel(session, _feed.Cockpit);
            var brush = session.SessionStatus is SessionStatus.Busy or SessionStatus.WorkingBackground
                && !_feed.Cockpit.Sessions.Contains(session) ? "CockpitStatusBackgroundBrush" : session.SessionStatusBrushKey;
            var dot = new Ellipse { Width = 7, Height = 7, Margin = new Thickness(0, 5, 9, 0), VerticalAlignment = VerticalAlignment.Top };
            dot.Bind(Shape.FillProperty, dot.GetResourceObservable(brush));
            var text = new StackPanel { Spacing = 3 };
            text.Children.Add(_Text(card.Title));
            text.Children.Add(_Text(card.Project, "CockpitTextSecondaryBrush"));
            text.Children.Add(_Text(session.SessionStatusLabel, brush));
            if (session.Statusline.Length > 0)
            {
                text.Children.Add(_Text(session.Statusline, "CockpitTextSecondaryBrush"));
            }
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            row.Children.Add(dot);
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            var button = new Button { Content = row, Tag = session, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            button.Classes.Add("Ghost");
            button.Click += (_, _) => _feed.Cockpit.SelectSessionCommand.Execute(session);
            var selection = new Border { Child = button, CornerRadius = new CornerRadius(6) };
            selection.Classes.Add("sessionItem");
            var selected = ReferenceEquals(_feed.Cockpit.SelectedSession, session);
            selection.Classes.Set("active", selected);
            if (selected)
            {
                selection.Bind(Border.BackgroundProperty, selection.GetResourceObservable("CockpitAccentSelectionBrush"));
            }
            _body.Children.Add(selection);
        }
        _body.Children.Add(_Heading("Services"));
        foreach (var status in _feed.Cockpit.PluginStatusTexts)
        {
            _body.Children.Add(_StatusRow(MaterialIconKind.CloudCheckOutline, status.PluginId, status.Text));
        }
        _body.Children.Add(_StatusRow(MaterialIconKind.MicrophoneOutline, "Audio",
            _feed.Cockpit.IsAudioAvailable ? "Available" : _feed.Cockpit.AudioUnavailableMessage));
        var open = new Button { Content = "Open Cockpit" };
        open.Classes.Add("Accent");
        open.Click += (_, _) => (Application.Current as App)?.ShowMainWindow();
        _body.Children.Add(open);
    }

    private static Grid _StatusRow(MaterialIconKind icon, string name, string status)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,*"), ColumnSpacing = 8, Margin = new Thickness(8, 4) };
        row.Children.Add(CockpitIcons.Icon(icon, 16));
        var title = _Text(name);
        Grid.SetColumn(title, 1);
        row.Children.Add(title);
        var value = _Text(status, "CockpitTextSecondaryBrush");
        value.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(value, 2);
        row.Children.Add(value);
        return row;
    }

    private static TextBlock _Heading(string text) => new() { Text = text, FontSize = 11, Margin = new Thickness(8, 6, 0, 0) };

    private static TextBlock _Text(string text, string brush = "CockpitTextPrimaryBrush")
    {
        var label = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        label.Bind(TextBlock.ForegroundProperty, label.GetResourceObservable(brush));
        return label;
    }
}
