using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Cockpit.Core.Abstractions.Diagrams;
using Cockpit.Core.Abstractions.Whiteboard;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Diagram;

// AC-849: the operator's open questions on this surface's objects, each already landed as a "📍 pin N" reference in
// the coupled session — same shared-registry shape as ActivityStrip (read that one first), but "Close" is the
// operator's own call that a pin was answered, never a system-detected one (there is no reply-correlation channel).
internal sealed class PinStrip : Border
{
    private readonly string _surfaceId;
    private readonly bool _whiteboard;
    private readonly IDiagramAccessRegistry? _diagramRegistry;
    private readonly IWhiteboardAccessRegistry? _whiteboardRegistry;
    private readonly Action<string>? _onJumpToObject;
    private readonly StackPanel _rows = new() { Spacing = 2 };
    private readonly TextBlock _emptyLabel;

    public PinStrip(ICockpitHost host, string surfaceId, bool whiteboard, Action<string>? onJumpToObject)
    {
        _surfaceId = surfaceId;
        _whiteboard = whiteboard;
        _onJumpToObject = onJumpToObject;
        _diagramRegistry = whiteboard ? null : host.Services.GetService(typeof(IDiagramAccessRegistry)) as IDiagramAccessRegistry;
        _whiteboardRegistry = whiteboard ? host.Services.GetService(typeof(IWhiteboardAccessRegistry)) as IWhiteboardAccessRegistry : null;

        Height = 90;
        Background = _Brush("CockpitSecondaryBgBrush");
        BorderBrush = _Brush("CockpitHairlineBrush");
        BorderThickness = new Thickness(0, 1, 0, 0);

        _emptyLabel = new TextBlock
        {
            Text = "No pins yet.",
            FontSize = 11,
            Opacity = 0.6,
            Margin = new Thickness(12, 6),
        };

        var header = new TextBlock { Text = "Pins", FontSize = 11, FontWeight = FontWeight.SemiBold, Margin = new Thickness(12, 6, 12, 2) };
        DockPanel.SetDock(header, Dock.Top);

        Child = new DockPanel
        {
            Children =
            {
                header,
                new ScrollViewer { Content = new Panel { Children = { _emptyLabel, _rows } } },
            },
        };

        if (_diagramRegistry is not null)
        {
            _diagramRegistry.PinsChanged += _OnPinsChanged;
        }

        if (_whiteboardRegistry is not null)
        {
            _whiteboardRegistry.PinsChanged += _OnPinsChanged;
        }

        DetachedFromVisualTree += (_, _) =>
        {
            if (_diagramRegistry is not null)
            {
                _diagramRegistry.PinsChanged -= _OnPinsChanged;
            }

            if (_whiteboardRegistry is not null)
            {
                _whiteboardRegistry.PinsChanged -= _OnPinsChanged;
            }
        };

        _Refresh();
    }

    private void _OnPinsChanged(string surfaceId)
    {
        if (surfaceId != _surfaceId)
        {
            return;
        }

        Dispatcher.UIThread.Post(_Refresh);
    }

    private void _Refresh()
    {
        var rows = _whiteboard
            ? (_whiteboardRegistry?.Pins(_surfaceId) ?? []).Select(_WhiteboardRow).ToList()
            : (_diagramRegistry?.Pins(_surfaceId) ?? []).Select(_DiagramRow).ToList();

        _emptyLabel.IsVisible = rows.Count == 0;
        _rows.IsVisible = rows.Count > 0;
        _rows.Children.Clear();
        for (var i = rows.Count - 1; i >= 0; i--)
        {
            _rows.Children.Add(rows[i]);
        }
    }

    private Control _DiagramRow(DiagramPin pin) =>
        _Row(pin.When, pin.Question, pin.ObjectKey, pin.Closed, () => _diagramRegistry?.ClosePin(_surfaceId, pin.Id));

    private Control _WhiteboardRow(WhiteboardPin pin) =>
        _Row(pin.When, pin.Question, pin.ObjectId, pin.Closed, () => _whiteboardRegistry?.ClosePin(_surfaceId, pin.Id));

    // One row, diagram or whiteboard alike: a "Close" the operator presses once they judge the agent answered it —
    // disabled once closed, same disabled-once-done shape as ActivityStrip's "Revert".
    private Control _Row(DateTime when, string question, string objectKey, bool closed, Action close)
    {
        var closeButton = new Button { Content = "Close", Classes = { "Compact", "Subtle" }, FontSize = 10, IsEnabled = !closed };
        ToolTip.SetTip(closeButton, closed ? "This pin is already closed." : "Close this pin — the question was answered.");
        closeButton.PointerPressed += (_, e) => e.Handled = true;
        closeButton.Click += (_, _) => close();
        DockPanel.SetDock(closeButton, Dock.Right);

        var lines = new StackPanel
        {
            Opacity = closed ? 0.55 : 1,
            Children =
            {
                new TextBlock { Text = $"{when:HH:mm:ss}{(closed ? " · closed" : "")}", FontSize = 10, Opacity = 0.55 },
                new TextBlock
                {
                    Text = question,
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 480,
                    TextDecorations = closed ? TextDecorations.Strikethrough : null,
                },
            },
        };

        var row = new DockPanel { Children = { closeButton, lines } };
        var border = new Border
        {
            Padding = new Thickness(6, 3),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = row,
        };

        border.PointerPressed += (_, e) =>
        {
            if (!e.Handled)
            {
                _onJumpToObject?.Invoke(objectKey);
            }
        };

        return border;
    }

    private static IBrush? _Brush(string resourceKey) =>
        Application.Current?.FindResource(resourceKey) as IBrush;
}
