using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Cockpit.Core.Abstractions.Toasts;
using Cockpit.Core.Diagnostics;
using Cockpit.Core.HeavyRuns;
using Cockpit.Core.Toasts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cockpit.App.Controls;

internal sealed class DiskStorageStatus : Button
{
    private readonly TextBlock _text = new() { FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
    private IMachineResources? _resources;
    private IDockerStorageMaintenance? _maintenance;
    private Flyout? _flyout;
    private TextBlock? _readings;
    private TextBlock? _summary;
    private TextBlock? _history;
    private Button? _cleanup;
    private Button? _compact;
    private bool _busy;

    public DiskStorageStatus()
    {
        Classes.Add("statusMeter");
        Padding = new Thickness(8, 2);
        Margin = new Thickness(0, 0, 8, 0);
        Content = _text;
        ToolTip.SetTip(this, "Disk space and Docker storage maintenance");
        AttachedToVisualTree += (_, _) => _Attach();
        DetachedFromVisualTree += (_, _) => _Detach();
        Click += (_, _) =>
        {
            _flyout?.Hide();
            _flyout = new Flyout { Placement = PlacementMode.Top, Content = _BuildPanel() };
            _flyout.ShowAt(this);
        };
    }

    private void _Attach()
    {
        _Detach();
        _resources = Program.Services?.GetService<IMachineResources>();
        _maintenance = Program.Services?.GetService<IDockerStorageMaintenance>();
        if (_resources is not null)
            _resources.Sampled += _OnChanged;
        if (_maintenance is not null)
            _maintenance.Changed += _OnChanged;
        _Refresh();
    }

    private void _Detach()
    {
        if (_resources is not null)
            _resources.Sampled -= _OnChanged;
        if (_maintenance is not null)
            _maintenance.Changed -= _OnChanged;
        _resources = null;
        _maintenance = null;
        _flyout?.Hide();
        _flyout = null;
        _readings = _summary = _history = null;
        _cleanup = _compact = null;
    }

    private void _OnChanged() => Dispatcher.UIThread.Post(_Refresh);

    private void _Refresh()
    {
        var disks = _resources?.Disks ?? [];
        _text.Text = disks.Count == 0
            ? "Disk: awaiting readings"
            : string.Join(" · ", disks.Select(disk => $"{disk.Name} {_Bytes(disk.FreeBytes)} free{_Forecast(disk, true)}"));
        _text.Foreground = disks.Any(disk => disk.Pressure == DiskPressure.Critical) ? Brushes.IndianRed
            : disks.Any(disk => disk.Pressure == DiskPressure.Elevated) ? Brushes.DarkOrange : null;
        _RefreshPanel();
    }

    private Control _BuildPanel()
    {
        var panel = new StackPanel { Spacing = 8, MinWidth = 320, MaxWidth = 520, Margin = new Thickness(8) };
        panel.Children.Add(new TextBlock { Text = "Disk space and Docker storage", FontWeight = FontWeight.SemiBold });
        _readings = _Detail("");
        _summary = _Detail("");
        _history = _Detail("");
        panel.Children.Add(_readings);
        panel.Children.Add(_summary);
        panel.Children.Add(_history);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        _cleanup = new Button { Content = "Clean up now" };
        _compact = new Button { Content = "Compact…" };
        _cleanup.Click += async (_, _) => await _RunAsync(() => _maintenance?.CleanupAsync() ?? Task.CompletedTask);
        _compact.Click += async (_, _) => await _RunAsync(() => _maintenance?.RequestCompactionAsync() ?? Task.CompletedTask);
        actions.Children.Add(_cleanup);
        actions.Children.Add(_compact);
        panel.Children.Add(actions);
        _RefreshPanel();
        return new ScrollViewer { Content = panel, MaxHeight = 480, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
    }

    private void _RefreshPanel()
    {
        if (_readings is null || _summary is null || _history is null || _cleanup is null || _compact is null)
            return;
        var disks = _resources?.Disks ?? [];
        _readings.Text = disks.Count == 0 ? "Awaiting disk readings." : string.Join("\n", disks.Select(disk =>
            $"{disk.Name}: {_Bytes(disk.FreeBytes)} free of {_Bytes(disk.TotalBytes)} · {disk.Pressure}{_Forecast(disk, false)}"));
        _summary.Text = _maintenance?.Summary ?? "Docker storage maintenance is unavailable.";
        _history.Text = string.Join("\n", _maintenance?.History.TakeLast(8) ?? []);
        _history.IsVisible = !string.IsNullOrEmpty(_history.Text);
        _cleanup.IsEnabled = _maintenance is not null && !_busy;
        _compact.IsEnabled = _maintenance?.CanCompact == true && !_busy;
    }

    private async Task _RunAsync(Func<Task> action)
    {
        if (_busy)
            return;
        _busy = true;
        _Refresh();
        try
        {
            await action();
        }
        catch (Exception exception)
        {
            Program.Services?.GetService<ILoggerFactory>()?.CreateLogger<DiskStorageStatus>()
                .LogWarning(exception, "Docker storage maintenance failed.");
            Program.Services?.GetService<IToastService>()?.Show($"Docker storage maintenance failed: {exception.Message}", ToastSeverity.Error);
        }
        finally
        {
            _busy = false;
            _Refresh();
        }
    }

    private static TextBlock _Detail(string text) => new() { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap };

    private static string _Bytes(long bytes) => bytes >= 1024L * 1024 * 1024
        ? $"{bytes / (1024d * 1024 * 1024):0.#} GiB" : $"{bytes / (1024d * 1024):0.#} MiB";

    private static string _Forecast(DiskSpaceSnapshot disk, bool pressureOnly)
    {
        if ((pressureOnly && disk.Pressure == DiskPressure.Normal) || disk.TimeToFull is not { } eta)
            return "";
        var duration = eta.TotalDays >= 1 ? $"{eta.TotalDays:0.#}d"
            : eta.TotalHours >= 1 ? $"{eta.TotalHours:0.#}h" : $"{Math.Max(1, Math.Ceiling(eta.TotalMinutes))}m";
        return $" · full in {duration}";
    }
}
