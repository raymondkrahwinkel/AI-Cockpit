using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Cockpit.App.Controls;

/// <summary>
/// Applies the cockpit's custom window chrome to any <see cref="Window"/>: it drops the OS title bar and
/// caption buttons (Avalonia 12 <see cref="WindowDecorations.BorderOnly"/>) while keeping a resizable
/// border, and wraps the window's content under a hairline title bar with its own caption buttons. Shared
/// so every window — the plugin dialogs and the app's own dialogs/main window — looks the same. Dialogs
/// get a Close button only; the main window opts into minimize/maximize.
/// </summary>
internal static class CockpitWindowChrome
{
    /// <param name="onSettings">
    /// When given, a gear appears left of the caption buttons and runs this — how a plugin's dialog offers its own
    /// settings (#: settings from anywhere). Omitted, the title bar looks exactly as it did.
    /// </param>
    public static void Apply(Window window, string? title = null, bool includeMinimize = false, bool includeMaximize = false, bool closeOnEscape = true, Action? onSettings = null)
    {
        window.WindowDecorations = WindowDecorations.BorderOnly;
        window.ExtendClientAreaToDecorationsHint = true;
        if (_Brush("CockpitPanelBgBrush") is { } background)
        {
            window.Background = background;
        }

        if (closeOnEscape)
        {
            // Esc closes a dialog. A bubbling handler, so a control that legitimately uses Esc first — an open
            // dropdown, or a palette with its own Esc handling — consumes it and the dialog stays open.
            window.AddHandler(InputElement.KeyDownEvent, (_, e) =>
            {
                if (e.Key == Key.Escape && !e.Handled)
                {
                    e.Handled = true;
                    window.Close();
                }
            });
        }

        var body = window.Content as Control ?? new Panel();
        // Detach the existing content before re-parenting it under the chrome, or Avalonia throws while the
        // control is briefly a child of two parents.
        window.Content = null;
        window.Content = _ChromeRoot(window, title ?? window.Title ?? string.Empty, body, includeMinimize, includeMaximize, onSettings);
    }

    private static Control _ChromeRoot(Window window, string title, Control body, bool includeMinimize, bool includeMaximize, Action? onSettings)
    {
        var root = new DockPanel();
        var titleBar = _TitleBar(window, title, includeMinimize, includeMaximize, onSettings);
        DockPanel.SetDock(titleBar, Dock.Top);
        root.Children.Add(titleBar);
        root.Children.Add(body);
        return root;
    }

    private static Control _TitleBar(Window window, string title, bool includeMinimize, bool includeMaximize, Action? onSettings)
    {
        var titleText = new TextBlock
        {
            Text = title,
            FontWeight = FontWeight.SemiBold,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(14, 0, 0, 0),
        };

        var captionButtons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Stretch };

        // The gear sits before the caption buttons, so closing a window stays where the hand already goes.
        if (onSettings is not null)
        {
            var settings = _CaptionButton(CockpitIcons.Gear());
            ToolTip.SetTip(settings, "Settings");
            settings.Click += (_, _) => onSettings();
            captionButtons.Children.Add(settings);
        }

        if (includeMinimize)
        {
            var minimize = _CaptionButton("—");
            minimize.Click += (_, _) => window.WindowState = WindowState.Minimized;
            captionButtons.Children.Add(minimize);
        }

        if (includeMaximize)
        {
            var maximize = _CaptionButton(_MaximizeGlyph(window.WindowState));
            maximize.Click += (_, _) => _ToggleMaximize(window);
            captionButtons.Children.Add(maximize);

            // Keep the glyph in sync with the state (maximize ▢ vs restore ❐), whichever way it changed.
            window.PropertyChanged += (_, e) =>
            {
                if (e.Property == Window.WindowStateProperty)
                {
                    maximize.Content = _MaximizeGlyph(window.WindowState);
                }
            };
        }

        var close = _CaptionButton("✕");
        close.Click += (_, _) => window.Close();
        captionButtons.Children.Add(close);

        var bar = new DockPanel { Height = 38 };
        DockPanel.SetDock(captionButtons, Dock.Right);
        bar.Children.Add(captionButtons);
        bar.Children.Add(titleText);

        var wrapper = new Border
        {
            Background = _Brush("CockpitSecondaryBgBrush"),
            BorderBrush = _Brush("CockpitHairlineBrush"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = bar,
        };

        // Drag the window by the title bar; a double-click maximizes/restores it (where that is allowed),
        // and a press on a caption button is left to the button.
        wrapper.PointerPressed += (_, e) =>
        {
            if (e.Source is Button)
            {
                return;
            }

            if (includeMaximize && e.ClickCount == 2)
            {
                _ToggleMaximize(window);
                return;
            }

            window.BeginMoveDrag(e);
        };

        return wrapper;
    }

    private static void _ToggleMaximize(Window window) =>
        window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private static string _MaximizeGlyph(WindowState state) => state == WindowState.Maximized ? "❐" : "▢";

    // A uniform caption button: same width, font size and centred glyph so the buttons line up regardless
    // of each glyph's own metrics.
    private static Button _CaptionButton(object content) => new()
    {
        Content = content,
        Classes = { "Subtle" },
        FontSize = 13,
        Width = 46,
        Padding = new Thickness(0),
        VerticalAlignment = VerticalAlignment.Stretch,
        HorizontalContentAlignment = HorizontalAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center,
    };

    private static IBrush? _Brush(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is IBrush brush ? brush : null;
}
