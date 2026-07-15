using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Cockpit.Plugins.Abstractions.Widgets;

namespace Cockpit.Plugin.Widgets;

/// <summary>
/// The time and date. The simplest widget there is, and deliberately so: it declares no settings form, which
/// is what proves the pane's ⚙ is really gated on having one rather than always drawn.
/// </summary>
internal sealed class ClockWidget : UserControl
{
    private readonly TextBlock _time = new()
    {
        FontSize = 34,
        FontWeight = FontWeight.SemiBold,
        HorizontalAlignment = HorizontalAlignment.Center,
    };

    private readonly TextBlock _date = new()
    {
        FontSize = 13,
        Opacity = 0.7,
        HorizontalAlignment = HorizontalAlignment.Center,
    };

    private readonly DispatcherTimer _timer;

    public ClockWidget(IWidgetContext context)
    {
        Content = new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _time, _date },
        };

        _Tick();

        // Its own timer rather than the host's refresh: a clock is the one widget that has to update whether or
        // not anyone asked it to. RefreshRequested is still honoured, so the pane's ↻ does something visible.
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => _Tick();
        _timer.Start();
        context.RefreshRequested += (_, _) => _Tick();

        // The timer keeps a reference to this control, so it has to stop when the pane goes away or the widget
        // outlives the dashboard that held it.
        DetachedFromVisualTree += (_, _) => _timer.Stop();
    }

    private void _Tick()
    {
        var now = DateTimeOffset.Now;
        _time.Text = now.ToString("HH:mm:ss");
        _date.Text = now.ToString("dddd d MMMM yyyy");
    }
}
