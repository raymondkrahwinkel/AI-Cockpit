using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Cockpit.App.Controls;
using Cockpit.App.ViewModels;
using Cockpit.Plugins.Abstractions;
using Material.Icons;

namespace Cockpit.App.Views;

internal sealed class CompanionScreenshotView : StackPanel
{
    private const string RecipientKey = "screenshot.recipient";
    private readonly IPluginStorage _storage;
    private readonly CockpitViewModel _cockpit;
    private SessionPanelViewModel? _observedSession;
    private bool _isAttached;
    private readonly Button _assistant = new() { Classes = { "Subtle", "Row" } };
    private readonly Button _agent = new() { Classes = { "Subtle", "Row" } };
    private readonly Button _capture = new() { Content = CockpitIcons.Icon(MaterialIconKind.Monitor), Classes = { "Subtle", "Compact" } };
    private readonly Button _choose = new() { Content = CockpitIcons.Icon(MaterialIconKind.ChevronDown), Classes = { "Subtle", "Compact" } };

    public CompanionScreenshotView(IPluginStorage storage, CockpitViewModel cockpit,
        Func<Task> captureAssistant, Func<Task> captureSelected)
    {
        _storage = storage;
        _cockpit = cockpit;
        Orientation = Avalonia.Layout.Orientation.Horizontal;
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
        Children.Add(_capture);
        Children.Add(_choose);
        AutomationProperties.SetName(_capture, "Take a screenshot");
        AutomationProperties.SetName(_choose, "Screenshot recipient");
        ToolTip.SetTip(_choose, "Send screenshot to");
        ToolTip.SetShowOnDisabled(_capture, true);
        ToolTip.SetShowOnDisabled(_agent, true);
        var menu = new Flyout
        {
            FlyoutPresenterClasses = { "Card" },
            Content = new StackPanel
            {
                Width = 280,
                Children =
                {
                    new TextBlock { Text = "Send screenshot to", FontWeight = Avalonia.Media.FontWeight.SemiBold, Margin = new Thickness(10,6) },
                    _assistant,
                    _agent,
                },
            },
        };
        menu.Opening += (_, _) => Refresh();
        _choose.Flyout = menu;
        _assistant.Click += (_, _) => _Choose("assistant");
        _agent.Click += (_, _) => _Choose("selected");
        _capture.Click += async (_, _) =>
        {
            Refresh();
            if (!_capture.IsEffectivelyEnabled)
            {
                return;
            }

            await (_IsSelectedAgent ? captureSelected() : captureAssistant());
        };
        AttachedToVisualTree += (_, _) =>
        {
            _isAttached = true;
            _cockpit.PropertyChanged += _OnSelectionChanged;
            Refresh();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _isAttached = false;
            _cockpit.PropertyChanged -= _OnSelectionChanged;
            _WatchSession(null);
        };
        Refresh();
    }

    private bool _IsSelectedAgent => _storage.Get<string>(RecipientKey) == "selected";

    public void Refresh()
    {
        var session = _cockpit.SelectedSession;
        _WatchSession(_isAttached ? session : null);
        var refusal = session?.ScreenshotRefusalReason ?? (session is null ? "No agent is selected." : null);
        _assistant.Content = _RecipientRow(MaterialIconKind.Microphone, "Assistant", "Its standing conversation", !_IsSelectedAgent);
        _agent.Content = _RecipientRow(MaterialIconKind.RobotOutline, session?.Title ?? "Selected agent",
            refusal ?? (session is TtyViewModel ? "Paste the screenshot path into this terminal." : "Selected agent"), _IsSelectedAgent);
        AutomationProperties.SetName(_assistant, _IsSelectedAgent ? "Assistant" : "Assistant, selected");
        AutomationProperties.SetName(_agent, $"Selected agent: {session?.Title}{(_IsSelectedAgent ? ", selected" : string.Empty)}");
        _agent.IsEnabled = refusal is null;
        ToolTip.SetTip(_agent, refusal ?? "Send the screenshot to the selected agent.");
        _capture.IsEnabled = !_IsSelectedAgent || refusal is null;
        ToolTip.SetTip(_capture, !IsEnabled ? "Screen capture is not available on this platform."
            : _IsSelectedAgent ? refusal ?? $"Take a screenshot for {session?.Title}"
            : "Take a screenshot for Assistant");
    }

    private static Control _RecipientRow(MaterialIconKind iconKind, string title, string subtitle, bool selected)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 9 };
        row.Children.Add(CockpitIcons.Icon(iconKind, 15));
        var label = new TextBlock { Text = title, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var hint = new TextBlock
        {
            Text = subtitle,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            FontSize = 11,
        };
        hint.Bind(TextBlock.ForegroundProperty, hint.GetResourceObservable("CockpitTextFaintBrush"));
        var text = new StackPanel { Spacing = 3, Children = { label, hint } };
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        var check = CockpitIcons.Icon(MaterialIconKind.Check, 14);
        check.IsVisible = selected;
        Grid.SetColumn(check, 2);
        row.Children.Add(check);
        return row;
    }

    internal void OpenRecipients() => _choose.Flyout?.ShowAt(_choose);

    private void _OnSelectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CockpitViewModel.SelectedSession))
        {
            Refresh();
        }
    }

    private void _OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SessionPanelViewModel.Title) or nameof(SessionPanelViewModel.ScreenshotRefusalReason))
        {
            Refresh();
        }
    }

    private void _WatchSession(SessionPanelViewModel? session)
    {
        if (ReferenceEquals(_observedSession, session))
        {
            return;
        }

        if (_observedSession is { } previous)
        {
            previous.PropertyChanged -= _OnSessionChanged;
        }

        _observedSession = session;
        if (session is not null)
        {
            session.PropertyChanged += _OnSessionChanged;
        }
    }

    private void _Choose(string recipient)
    {
        _storage.Set(RecipientKey, recipient);
        Refresh();
        _choose.Flyout?.Hide();
    }
}
