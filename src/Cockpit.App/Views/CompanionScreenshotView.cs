using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.VisualTree;
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
    private readonly StackPanel _recipients = new() { Margin = new Thickness(6, 0, 6, 0) };
    private readonly Button _capture = new() { Content = CockpitIcons.Icon(MaterialIconKind.MonitorScreenshot, 18), Classes = { "companionStripItem" } };
    private readonly Button _choose = new() { Content = CockpitIcons.Icon(MaterialIconKind.MenuDown, 14), Classes = { "companionStripItem" } };
    private readonly Flyout _menu;
    private Button _agent = new();

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
        // AC-2190: the mockup's split — one 34px button and a 16px caret sharing their outer corners — and its
        // recipients pop-out under the split, 6px clear of the strip, with a head, the two rows and a hint at the foot.
        _menu = new Flyout
        {
            FlyoutPresenterClasses = { "Card", "companionPanel" },
            Content = new StackPanel
            {
                Width = 280,
                Children =
                {
                    CompanionPanelChrome.Header("Send screenshot to", () => _menu?.Hide()),
                    _recipients,
                    _Hint(),
                },
            },
        };
        _menu.Opening += (_, _) => Refresh();
        _menu.Closed += (_, _) => _choose.Classes.Set("open", false);
        _Shape();
        _choose.Click += (_, _) => OpenRecipients();
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
            if (this.FindAncestorOfType<StackPanel>() is { Name: "ToolsHost" } host)
            {
                Bind(OrientationProperty, new Avalonia.Data.Binding(nameof(Orientation)) { Source = host });
            }
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
        var assistant = CompanionPanelChrome.PickRow(MaterialIconKind.Microphone, "Assistant", "Its standing conversation", !_IsSelectedAgent);
        _agent = CompanionPanelChrome.PickRow(MaterialIconKind.RobotOutline, session?.Title ?? "Selected agent",
            refusal ?? (session is TtyViewModel ? "Paste the screenshot path into this terminal." : "Selected agent"), _IsSelectedAgent);
        assistant.Click += (_, _) => _Choose("assistant");
        _agent.Click += (_, _) => _Choose("selected");
        _recipients.Children.Clear();
        _recipients.Children.Add(assistant);
        _recipients.Children.Add(_agent);
        AutomationProperties.SetName(assistant, _IsSelectedAgent ? "Assistant" : "Assistant, selected");
        AutomationProperties.SetName(_agent, $"Selected agent: {session?.Title}{(_IsSelectedAgent ? ", selected" : string.Empty)}");
        ToolTip.SetShowOnDisabled(_agent, true);
        _agent.IsEnabled = refusal is null;
        if (!_agent.IsEnabled)
        {
            _agent.Opacity = 0.55;
        }
        ToolTip.SetTip(_agent, refusal ?? "Send the screenshot to the selected agent.");
        _capture.IsEnabled = !_IsSelectedAgent || refusal is null;
        ToolTip.SetTip(_capture, !IsEnabled ? "Screen capture is not available on this platform."
            : _IsSelectedAgent ? refusal ?? $"Take a screenshot for {session?.Title}"
            : "Take a screenshot for Assistant");
    }

    private static Control _Hint()
    {
        var hint = CompanionPanelChrome.Text("Change the selected agent under Status.", "CockpitTextSecondaryBrush", 11);
        hint.Margin = new Thickness(14, 6, 14, 10);

        return hint;
    }

    // The split follows the strip: side by side across a horizontal one, the caret under the button down a vertical one.
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        // The base constructor sets the orientation before this one has built its menu.
        if (change.Property == OrientationProperty && _menu is not null)
        {
            _Shape();
        }
    }

    private void _Shape()
    {
        var horizontal = Orientation == Avalonia.Layout.Orientation.Horizontal;
        _capture.CornerRadius = horizontal ? new CornerRadius(9, 0, 0, 9) : new CornerRadius(9, 9, 0, 0);
        _choose.CornerRadius = horizontal ? new CornerRadius(0, 9, 9, 0) : new CornerRadius(0, 0, 9, 9);
        _choose.MinWidth = horizontal ? 16 : 34;
        _choose.Width = horizontal ? 16 : 34;
        _choose.MinHeight = horizontal ? 34 : 16;
        _choose.Height = horizontal ? 34 : 16;
        // Clear of the strip's own edge: the strip pads its items by 4px inside a 1px hairline, and a pop-out keeps 6px off it.
        _menu.Placement = horizontal ? PlacementMode.BottomEdgeAlignedLeft : PlacementMode.RightEdgeAlignedTop;
        _menu.VerticalOffset = horizontal ? 11 : 0;
        _menu.HorizontalOffset = horizontal ? 0 : 11;
    }

    internal void OpenRecipients()
    {
        _choose.Classes.Set("open", true);
        _menu.ShowAt(this);
    }

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
        _menu.Hide();
    }
}
