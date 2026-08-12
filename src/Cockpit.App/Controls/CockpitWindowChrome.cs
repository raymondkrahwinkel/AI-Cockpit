using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Cockpit.Core.Configuration;
using Material.Icons;

namespace Cockpit.App.Controls;

// Applies the cockpit's custom window chrome to any `Window`: it drops the OS decorations entirely
// (`WindowDecorations.None` — AC-678: `BorderOnly`'s own resize border was a visible margin around every
// non-maximized window) and wraps the window's content under a hairline title bar with its own caption buttons.
// `WindowResizeGrip` gives a resizable window its edges and corners back. Shared so every window — the plugin
// dialogs and the app's own dialogs/main window — looks the same. Dialogs get a Close button only; the main
// window opts into minimize/maximize.
internal static class CockpitWindowChrome
{
    // The mockup's two title bars (cockpit-projects-flow-2026-07-21.html: .titlebar and .titlebar.dlg).
    // Weights are the closest real ones: the reference asks for 600/660, which a variable web font can hit
    // and the desktop UI font rounds to SemiBold either way.
    //
    // The dialog sizes below deliberately depart from it: transcribed literally they gave 97px of bar on a
    // dialog 229px tall, so the header outweighed what it introduced. Judged on rendered dialogs (AC-426).
    private const double DialogTitleFontSize = 15;
    private const double DialogSubtitleFontSize = 11.5;
    private const double WindowTitleFontSize = 15.5;
    // The brand mark at the head of the app's own bar, kept at the newer mockup's proportion to the name beside
    // it (wispslate-cockpit-2026-07-28.html asks for a 19px mark against a 13px name; ours is a 15.5px name).
    private const double AppMarkHeight = 22;
    private const double DialogCaptionButtonHeight = 26;
    // The explanation under a name is one or two lines in the reference; more than three and the bar has stopped
    // being a header. Bounded rather than trusted, for the same reason the name itself is.
    private const int SubtitleMaxLines = 3;

    // The room around the heading. It sits on the heading rather than on the bar, so the caption buttons keep
    // reaching the window's own edge: the reference is a web page whose "window" has no screen corner to aim
    // at, and inset close buttons cost a maximised window the corner the mouse can be thrown at blindly.
    private static readonly Thickness DialogPadding = new(20, 12);
    private static readonly Thickness WindowPadding = new(18, 14);

    // Decoded once rather than per window: the main window and the unlock window can both be standing.
    private static readonly Lazy<Bitmap> AppMark = new(() =>
        new Bitmap(AssetLoader.Open(new Uri("avares://Cockpit.App/Assets/BrandMark.png"))));

    // `title`:
    // The name in the bar. Ignored by `CockpitTitleBar.Window`: the app's own window is not named by
    // its caller, it carries the product's name and mark (AC-430).
    // `subtitle`:
    // The line under a dialog's name saying what it is for (the mockup's .tsub). Left out, the header is just
    // the name. Ignored by `CockpitTitleBar.Window`, which has no room for a second line.
    // `onSettings`:
    // When given, a gear appears left of the caption buttons and runs this — how a plugin's dialog offers its own
    // settings (#: settings from anywhere). Omitted, the title bar looks exactly as it did.
    public static void Apply(Window window, string? title = null, string? subtitle = null, CockpitTitleBar titleBar = CockpitTitleBar.Dialog, bool includeMinimize = false, bool includeMaximize = false, bool closeOnEscape = true, Action? onSettings = null)
    {
        window.WindowDecorations = WindowDecorations.None;
        window.ExtendClientAreaToDecorationsHint = true;
        WindowResizeGrip.Attach(window);
        if (_Brush("CockpitPanelBgBrush") is { } background)
        {
            window.Background = background;
        }

        if (titleBar == CockpitTitleBar.Dialog)
        {
            // Every dialog, rather than the three that remembered to ask (AC-428). A dialog is centred on its
            // owner and has nothing to drag it back by, so one that opens taller than the screen has put its
            // own buttons past the bottom edge — which is the same failure as a footer laid out past the right
            // edge, on the other axis. The app's own window is left alone: it is meant to fill the screen.
            DialogScreenClamp.Apply(window);
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
        window.Content = _ChromeRoot(window, title ?? window.Title ?? string.Empty, subtitle, titleBar, body, includeMinimize, includeMaximize, onSettings);
    }

    private static Control _ChromeRoot(Window window, string title, string? subtitle, CockpitTitleBar shape, Control body, bool includeMinimize, bool includeMaximize, Action? onSettings)
    {
        var root = new DockPanel();
        var titleBar = _TitleBar(window, title, subtitle, shape, includeMinimize, includeMaximize, onSettings);
        DockPanel.SetDock(titleBar, Dock.Top);
        root.Children.Add(titleBar);
        root.Children.Add(body);
        return root;
    }

    private static Control _TitleBar(Window window, string title, string? subtitle, CockpitTitleBar shape, bool includeMinimize, bool includeMaximize, Action? onSettings)
    {
        var isDialog = shape == CockpitTitleBar.Dialog;

        var captionButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            // A dialog's bar is as tall as its heading, so a stretched close button would be a column two lines
            // high. It hangs from the top of the heading instead — the reference's align-items: flex-start.
            VerticalAlignment = isDialog ? VerticalAlignment.Top : VerticalAlignment.Stretch,
            Margin = isDialog ? new Thickness(0, DialogPadding.Top, 0, 0) : default,
        };

        // The gear sits before the caption buttons, so closing a window stays where the hand already goes.
        if (onSettings is not null)
        {
            var settings = _CaptionButton(CockpitIcons.Gear(), isDialog);
            ToolTip.SetTip(settings, "Settings");
            settings.Click += (_, _) => onSettings();
            captionButtons.Children.Add(settings);
        }

        if (includeMinimize)
        {
            var minimize = _CaptionButton(CockpitIcons.Icon(MaterialIconKind.WindowMinimize), isDialog);
            minimize.Click += (_, _) => window.WindowState = WindowState.Minimized;
            captionButtons.Children.Add(minimize);
        }

        if (includeMaximize)
        {
            var maximize = _CaptionButton(_MaximizeIcon(window.WindowState), isDialog);
            maximize.Click += (_, _) => _ToggleMaximize(window);
            captionButtons.Children.Add(maximize);

            // Keep the icon in sync with the state (maximize vs restore), whichever way it changed.
            window.PropertyChanged += (_, e) =>
            {
                if (e.Property == Window.WindowStateProperty)
                {
                    maximize.Content = _MaximizeIcon(window.WindowState);
                }
            };
        }

        var close = _CaptionButton(CockpitIcons.Icon(MaterialIconKind.WindowClose), isDialog);
        close.Click += (_, _) => window.Close();
        captionButtons.Children.Add(close);

        var bar = new DockPanel();
        DockPanel.SetDock(captionButtons, Dock.Right);
        bar.Children.Add(captionButtons);
        bar.Children.Add(isDialog ? _DialogHeading(title, subtitle) : _WindowHeading());

        var wrapper = new Border
        {
            Background = _Brush("CockpitChromeBgBrush"),
            BorderBrush = _Brush("CockpitHairlineSoftBrush"),
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

    // A dialog's name at heading scale, with the line under it that says what the dialog is for. This is the
    // heading a dialog used to draw again in its own content — one place now, so the name is stated once.
    private static Control _DialogHeading(string title, string? subtitle)
    {
        var heading = new StackPanel
        {
            Spacing = 1,
            Margin = DialogPadding,
            VerticalAlignment = VerticalAlignment.Center,
        };

        heading.Children.Add(_NameLine(title, DialogTitleFontSize));

        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            heading.Children.Add(new TextBlock
            {
                Text = subtitle,
                FontSize = DialogSubtitleFontSize,
                Foreground = _Brush("CockpitTextFaintBrush"),
                TextWrapping = TextWrapping.Wrap,
                MaxLines = SubtitleMaxLines,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
        }

        return heading;
    }

    // The app window's line: the brand mark, then the product's name — so the window reads as the cockpit itself
    // rather than as one more dialog. The mark stood here as a plain accent dot until the product got one.
    private static Control _WindowHeading()
    {
        var mark = new Image
        {
            Source = AppMark.Value,
            Height = AppMarkHeight,
            // Uniform, and only the height is given: the mark is wider than it is tall, and squaring it off would
            // deform it. The bar's own height follows the heading, so it makes room for whatever this measures.
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // A 276x206 bitmap landing at a fraction of that size. The default filtering leaves the thin strokes in
        // the mark ragged against the chrome, which at this size is the whole of it.
        RenderOptions.SetBitmapInterpolationMode(mark, BitmapInterpolationMode.HighQuality);

        var heading = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 9,
            Margin = WindowPadding,
            VerticalAlignment = VerticalAlignment.Center,
        };

        heading.Children.Add(mark);
        heading.Children.Add(_BrandLine());

        return heading;
    }

    // The product's name in two strengths on one line — the mockup's `Wispslate <span>Cockpit</span>`. The second
    // word steps back so the bar states which app this is without shouting a brand at someone who came to look at
    // their own sessions. Inlines rather than two TextBlocks, so the pair trims and aligns as one line of text.
    private static Control _BrandLine()
    {
        var line = _Line(WindowTitleFontSize);
        line.Inlines =
        [
            new Run(CockpitProduct.Brand),
            new Run($" {CockpitProduct.Product}") { Foreground = _Brush("CockpitTextFaintBrush") },
        ];
        return line;
    }

    // The window's name, on exactly one line. A title is not always the app's own: a plugin supplies the one on
    // its dialog, and a title carrying newlines would otherwise make the bar as tall as it liked — the bar used to
    // be a fixed 38px, which bounded that by accident, and it now grows with its heading.
    private static TextBlock _NameLine(string title, double fontSize)
    {
        var line = _Line(fontSize);
        line.Text = title;
        return line;
    }

    private static TextBlock _Line(double fontSize) => new()
    {
        FontSize = fontSize,
        FontWeight = FontWeight.SemiBold,
        VerticalAlignment = VerticalAlignment.Center,
        MaxLines = 1,
        TextTrimming = TextTrimming.CharacterEllipsis,
        // The reference's -0.01em: a fraction of a pixel off each pair, which is why it scales with the size
        // rather than being a figure of its own.
        LetterSpacing = fontSize * -0.01,
    };

    private static void _ToggleMaximize(Window window) =>
        window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private static Control _MaximizeIcon(WindowState state) =>
        CockpitIcons.Icon(state == WindowState.Maximized ? MaterialIconKind.WindowRestore : MaterialIconKind.WindowMaximize);

    // A uniform caption button: same width, font size and centred glyph so the buttons line up regardless
    // of each glyph's own metrics. On the app window it fills the bar's height, so the pointer can be thrown
    // into the corner; on a dialog it keeps a button's height, because the bar there is a two-line heading.
    private static Button _CaptionButton(object content, bool isDialog) => new()
    {
        Content = content,
        Classes = { "Subtle" },
        FontSize = 13,
        Width = 46,
        Height = isDialog ? DialogCaptionButtonHeight : double.NaN,
        Padding = new Thickness(0),
        VerticalAlignment = isDialog ? VerticalAlignment.Top : VerticalAlignment.Stretch,
        HorizontalContentAlignment = HorizontalAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center,
    };

    private static IBrush? _Brush(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is IBrush brush ? brush : null;
}
