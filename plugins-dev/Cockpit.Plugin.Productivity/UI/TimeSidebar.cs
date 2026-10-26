using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Cockpit.Plugin.Productivity.UI;

// The sidebar's Tijd section, as v3 #09's `.railsw`: a quiet idle line with a small Start, or the running stopwatch
// with its red dot, a mono clock, Stop, the description and "project · gestart". Square, like the rest of the sidebar.
internal sealed class TimeSidebar : UserControl
{
    private readonly ProductivityController _controller;
    private readonly Action _openWorkspace;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private object? _shown = new();
    private TextBlock? _clock;

    public TimeSidebar(ProductivityController controller, Action openWorkspace)
    {
        _controller = controller;
        _openWorkspace = openWorkspace;
        Margin = new Thickness(2, 4, 2, 0);
        _controller.Changed += _Render;
        _timer.Tick += (_, _) => _Tick();
        _timer.Start();
        DetachedFromVisualTree += (_, _) =>
        {
            _timer.Stop();
            _controller.Changed -= _Render;
        };
        _Render();
    }

    // Rebuilt only when the stopwatch itself changes; the clock ticks on its own, so Stop is never swapped out from
    // under a press.
    private void _Render()
    {
        var stopwatch = _controller.Stopwatch;
        if (ReferenceEquals(stopwatch, _shown))
        {
            return;
        }

        _shown = stopwatch;
        _clock = null;
        Content = stopwatch is null ? _Idle() : _Running(stopwatch);
        _Tick();
    }

    private void _Tick()
    {
        if (_clock is not null && _controller.Stopwatch is { } stopwatch)
        {
            var elapsed = DateTimeOffset.Now - stopwatch.StartedAt;
            if (elapsed < TimeSpan.Zero)
            {
                elapsed = TimeSpan.Zero;
            }

            _clock.Text = $"{(int)elapsed.TotalHours}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
        }
    }

    private Control _Idle()
    {
        var row = new DockPanel();
        var start = _SmallButton("SidebarStart", "▶ Start")
            .Themed(Button.BorderBrushProperty, "CockpitHairlineBrush")
            .Themed(Button.BackgroundProperty, "CockpitPanelBgBrush");
        start.Click += (_, _) => _openWorkspace();
        DockPanel.SetDock(start, Dock.Right);
        row.Children.Add(start);
        row.Children.Add(new TextBlock
        {
            Text = "Geen stopwatch actief",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
        }
            .Themed(TextBlock.ForegroundProperty, "CockpitTextFaintBrush"));
        return _Box(row)
            .Themed(Border.BorderBrushProperty, "CockpitHairlineBrush")
            .Themed(Border.BackgroundProperty, "CockpitSecondaryBgBrush");
    }

    private Control _Running(Cockpit.Plugins.Abstractions.TimeTracking.CockpitRunningStopwatch stopwatch)
    {
        // `.rec`: a 9 px red dot in a 3 px ring of its own colour.
        var rec = new Border
        {
            Width = 15,
            Height = 15,
            CornerRadius = new CornerRadius(7.5),
            VerticalAlignment = VerticalAlignment.Center,
            Background = _Tint("CockpitStatusErrorColor", 0.2),
            Child = Ui.Dot("CockpitStatusErrorBrush", 9),
        };
        ((Ellipse)rec.Child).HorizontalAlignment = HorizontalAlignment.Center;
        _clock = new TextBlock
        {
            FontFamily = new FontFamily("Cascadia Mono,Consolas,monospace"),
            FontWeight = FontWeight.Bold,
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
        }.Themed(TextBlock.ForegroundProperty, "CockpitStatusBusyBrush");

        // The label carries the red itself: the theme's button template paints its own foreground over the button's.
        var stop = _SmallButton("StopStopwatch", "■ Stop");
        stop.Content = new TextBlock { Text = "■ Stop", FontSize = 12 }.Themed(TextBlock.ForegroundProperty, "CockpitStatusErrorBrush");
        stop.BorderBrush = _Tint("CockpitStatusErrorColor", 0.4);
        stop.Click += async (_, _) => await _controller.StopStopwatchAsync();

        var row = new DockPanel();
        DockPanel.SetDock(stop, Dock.Right);
        row.Children.Add(stop);
        row.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { rec, _clock } });

        var body = new StackPanel { Children = { row } };
        body.Children.Add(new TextBlock { Text = stopwatch.Description, TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 4, 0, 0) }
            .Themed(TextBlock.ForegroundProperty, "CockpitTextSecondaryBrush"));
        body.Children.Add(new TextBlock
        {
            Text = $"{_controller.Names.Resolve(stopwatch.ProjectId).Label} · gestart {stopwatch.StartedAt.ToLocalTime():HH:mm}",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11,
        }.Themed(TextBlock.ForegroundProperty, "CockpitTextFaintBrush"));

        var box = _Box(body).Themed(Border.BackgroundProperty, "CockpitStatusBusyChipTintBrush");
        box.BorderBrush = _Tint("CockpitStatusBusyColor", 0.33);
        return box;
    }

    // `.railsw`: a 1 px border, padding 7/9.
    private static Border _Box(Control child) =>
        new() { Child = child, Padding = new Thickness(9, 7), BorderThickness = new Thickness(1) };

    // `.btn.sm`: padding 3/9, 12 px, a 1 px border.
    private static Button _SmallButton(string name, string text) =>
        new()
        {
            Name = name,
            Content = text,
            FontSize = 12,
            Padding = new Thickness(9, 3),
            BorderThickness = new Thickness(1),
            CornerRadius = default,
            VerticalAlignment = VerticalAlignment.Center,
        };

    // A theme colour at a fixed strength, as the mockup's `#D6454566`-style borders; follows a theme switch.
    private SolidColorBrush _Tint(string colorKey, double opacity)
    {
        var brush = new SolidColorBrush { Opacity = opacity };
        brush.Bind(SolidColorBrush.ColorProperty, this.GetResourceObservable(colorKey));
        return brush;
    }
}
