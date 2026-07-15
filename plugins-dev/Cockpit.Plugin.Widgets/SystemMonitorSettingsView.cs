using Avalonia.Controls;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Widgets;

namespace Cockpit.Plugin.Widgets;

/// <summary>
/// One System Monitor instance's settings: which of the three readings it shows. Implements
/// <see cref="IPluginSettingsView"/>, so the host wraps it in its standard Save/Close footer — the widget
/// supplies the content, never the window.
/// </summary>
/// <remarks>
/// Reads and writes through the instance's own <see cref="IWidgetContext.Storage"/>, which is what keeps two
/// monitors on one dashboard from sharing a selection.
/// </remarks>
internal sealed class SystemMonitorSettingsView : UserControl, IPluginSettingsView
{
    private readonly IWidgetContext _context;
    private readonly CheckBox _cpu = new() { Content = "CPU" };
    private readonly CheckBox _memory = new() { Content = "Memory" };
    private readonly CheckBox _disk = new() { Content = "Disk" };

    public SystemMonitorSettingsView(IWidgetContext context)
    {
        _context = context;

        var metrics = context.Storage.Get<SystemMonitorMetrics>(SystemMonitorMetrics.StorageKey) ?? SystemMonitorMetrics.Default;
        _cpu.IsChecked = metrics.ShowCpu;
        _memory.IsChecked = metrics.ShowMemory;
        _disk.IsChecked = metrics.ShowDisk;

        Content = new StackPanel
        {
            Spacing = 10,
            Margin = new Avalonia.Thickness(4),
            Children =
            {
                new TextBlock { Text = "Show", FontWeight = Avalonia.Media.FontWeight.SemiBold },
                _cpu,
                _memory,
                _disk,
                new TextBlock
                {
                    Text = "Clearing all three shows all three — an empty pane reads as broken.",
                    FontSize = 12,
                    Opacity = 0.7,
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                },
            },
        };
    }

    public bool Save()
    {
        _context.Storage.Set(SystemMonitorMetrics.StorageKey, new SystemMonitorMetrics
        {
            ShowCpu = _cpu.IsChecked == true,
            ShowMemory = _memory.IsChecked == true,
            ShowDisk = _disk.IsChecked == true,
        }.OrDefaultWhenEmpty());

        return true;
    }
}
