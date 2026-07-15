using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Cockpit.Plugins.Abstractions.Widgets;

namespace Cockpit.Plugin.Widgets;

/// <summary>
/// CPU, memory and disk, whichever of the three this instance is configured to show. The widget with a
/// settings form, so the ⚙ path is proven rather than assumed: opening it, saving it and seeing this pane
/// change is the end-to-end test of per-instance config.
/// </summary>
internal sealed class SystemMonitorWidget : UserControl
{
    private readonly IWidgetContext _context;
    private readonly StackPanel _rows = new() { Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
    private readonly DispatcherTimer _timer;

    public SystemMonitorWidget(IWidgetContext context)
    {
        _context = context;
        Content = _rows;
        _Render();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) => _Render();
        _timer.Start();

        // Raised by the pane's ↻ and after the settings form saves — which is how a changed metric selection
        // reaches this view without the widget watching its own storage.
        context.RefreshRequested += (_, _) => _Render();
        DetachedFromVisualTree += (_, _) => _timer.Stop();
    }

    private void _Render()
    {
        var metrics = (_context.Storage.Get<SystemMonitorMetrics>(SystemMonitorMetrics.StorageKey) ?? SystemMonitorMetrics.Default)
            .OrDefaultWhenEmpty();

        _rows.Children.Clear();
        if (metrics.ShowCpu)
        {
            _rows.Children.Add(_Row("CPU", SystemUsage.CpuPercent()));
        }

        if (metrics.ShowMemory)
        {
            _rows.Children.Add(_Row("Memory", SystemUsage.MemoryPercent()));
        }

        if (metrics.ShowDisk)
        {
            _rows.Children.Add(_Row("Disk", SystemUsage.DiskPercent()));
        }
    }

    private static Control _Row(string label, double percent)
    {
        var bar = new ProgressBar { Minimum = 0, Maximum = 100, Value = percent, Height = 6 };
        var caption = new DockPanel
        {
            Children =
            {
                new TextBlock { Text = $"{percent:0}%", FontSize = 12, Opacity = 0.7, [DockPanel.DockProperty] = Dock.Right },
                new TextBlock { Text = label, FontSize = 12 },
            },
        };

        return new StackPanel { Spacing = 3, Children = { caption, bar } };
    }
}
