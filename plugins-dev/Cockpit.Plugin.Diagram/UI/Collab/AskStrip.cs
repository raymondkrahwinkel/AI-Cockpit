using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Cockpit.Plugin.Diagram.Collab;

// AC-910: the operator's own log of asks on this surface, one class for diagram/whiteboard/wireframe (replaces
// AC-849's PinStrip and its `bool whiteboard` flag, AC-885). Registry-free — a plain list the body feeds, since an
// ask has exactly one reader and dies with the window. Unlike ActivityStrip, hidden until the first ask (criterion 11).
internal sealed class AskStrip : Border
{
    private readonly List<_Entry> _entries = [];
    private readonly Action<string>? _onJumpToObject;
    private readonly StackPanel _rows = new() { Spacing = 2 };

    public AskStrip(Action<string>? onJumpToObject)
    {
        _onJumpToObject = onJumpToObject;

        Height = 90;
        IsVisible = false;
        Background = _Brush("CockpitSecondaryBgBrush");
        BorderBrush = _Brush("CockpitHairlineBrush");
        BorderThickness = new Thickness(0, 1, 0, 0);

        var header = new TextBlock { Text = "Asked", FontSize = 11, FontWeight = FontWeight.SemiBold, Margin = new Thickness(12, 6, 12, 2) };
        DockPanel.SetDock(header, Dock.Top);

        Child = new DockPanel
        {
            Children = { header, new ScrollViewer { Content = _rows } },
        };
    }

    // `objectKey` is whatever the surface's own jump callback expects (a diagram HoldKey, a whiteboard object Guid
    // string, a wireframe component id) — never what went into the message itself (AC-910's rule against a
    // whiteboard Guid there), so it stays private to this strip's own click-to-jump.
    public void Add(string question, string? objectKey)
    {
        _entries.Add(new _Entry(DateTime.Now, question, objectKey, handled: false));
        _Refresh();
    }

    private void _Refresh()
    {
        IsVisible = _entries.Count > 0;
        _rows.Children.Clear();
        for (var i = _entries.Count - 1; i >= 0; i--)
        {
            _rows.Children.Add(_Row(_entries[i]));
        }
    }

    // One row: a "Handled" the operator presses once they judge the agent acted on it — purely their own call, the
    // same reasoning PinStrip's "Close" carried, since there is no reply-correlation channel to detect it for them.
    private Control _Row(_Entry entry)
    {
        var handledButton = new Button { Content = "Handled", Classes = { "Compact", "Subtle" }, FontSize = 10, IsEnabled = !entry.Handled };
        ToolTip.SetTip(handledButton, entry.Handled ? "Already marked handled." : "Mark this as handled.");
        handledButton.PointerPressed += (_, e) => e.Handled = true;
        handledButton.Click += (_, _) =>
        {
            entry.Handled = true;
            _Refresh();
        };
        DockPanel.SetDock(handledButton, Dock.Right);

        var lines = new StackPanel
        {
            Opacity = entry.Handled ? 0.55 : 1,
            Children =
            {
                new TextBlock { Text = $"{entry.When:HH:mm:ss}{(entry.Handled ? " · handled" : "")}", FontSize = 10, Opacity = 0.55 },
                new TextBlock
                {
                    Text = entry.Question,
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 480,
                    TextDecorations = entry.Handled ? TextDecorations.Strikethrough : null,
                },
            },
        };

        var row = new DockPanel { Children = { handledButton, lines } };
        var border = new Border
        {
            Padding = new Thickness(6, 3),
            Cursor = entry.ObjectKey is not null ? new Cursor(StandardCursorType.Hand) : Cursor.Default,
            Child = row,
        };

        if (entry.ObjectKey is { } key)
        {
            border.PointerPressed += (_, e) =>
            {
                if (!e.Handled)
                {
                    _onJumpToObject?.Invoke(key);
                }
            };
        }

        return border;
    }

    private static IBrush? _Brush(string resourceKey) =>
        Application.Current?.FindResource(resourceKey) as IBrush;

    private sealed class _Entry(DateTime when, string question, string? objectKey, bool handled)
    {
        public DateTime When { get; } = when;

        public string Question { get; } = question;

        public string? ObjectKey { get; } = objectKey;

        public bool Handled { get; set; } = handled;
    }
}
