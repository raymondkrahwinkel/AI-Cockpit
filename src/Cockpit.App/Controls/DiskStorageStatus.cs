using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Cockpit.Core.Abstractions.Toasts;
using Cockpit.Core.Diagnostics;
using Cockpit.Core.HeavyRuns;
using Cockpit.Core.Toasts;
using Material.Icons;
using Material.Icons.Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cockpit.App.Controls;

internal sealed class DiskStorageStatus : Button
{
    private readonly TextBlock _text = new() { FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
    private readonly MaterialIcon _icon = new() { Kind = MaterialIconKind.Harddisk, Width = 13, Height = 13, VerticalAlignment = VerticalAlignment.Center };
    private readonly MaterialIcon _chevron = new() { Kind = MaterialIconKind.ChevronUp, Width = 10, Height = 10, Margin = new Thickness(0, 2, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private IMachineResources? _resources;
    private IDockerStorageMaintenance? _maintenance;
    private Flyout? _flyout;
    private TextBlock? _headline;
    private StackPanel? _rows;
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
        _chevron.Bind(TemplatedControl.ForegroundProperty, this.GetResourceObservable("CockpitTextFaintBrush"));
        Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 6,
            Children = { _icon, _text, _chevron },
        };
        AttachedToVisualTree += (_, _) => _Attach();
        DetachedFromVisualTree += (_, _) => _Detach();
        Click += (_, _) =>
        {
            _flyout?.Hide();
            var flyout = _flyout = new Flyout { Placement = PlacementMode.Top, Content = _BuildPanel() };
            flyout.Closed += (_, _) => _chevron.Kind = MaterialIconKind.ChevronUp;
            _chevron.Kind = MaterialIconKind.ChevronDown;
            flyout.ShowAt(this);
        };
    }

    // The scenes hand it fixed readings; a live attach reads the services again.
    internal void Use(IMachineResources resources, IDockerStorageMaintenance maintenance)
    {
        (_resources, _maintenance) = (resources, maintenance);
        _Refresh();
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
        _headline = _summary = _history = null;
        _rows = null;
        _cleanup = _compact = null;
    }

    private void _OnChanged() => Dispatcher.UIThread.Post(_Refresh);

    private void _Refresh()
    {
        var disks = _resources?.Disks ?? [];
        var (figure, brushKey) = Headline(disks);
        _text.Text = figure;
        _icon.Bind(TemplatedControl.ForegroundProperty, this.GetResourceObservable(brushKey));
        _text.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable(brushKey));
        ToolTip.SetTip(this, disks.Count == 0
            ? "Disk space and Docker storage — awaiting readings"
            : string.Join(" · ", disks.Select(disk => $"{disk.Name} {_Bytes(disk.FreeBytes)} free")));
        _RefreshPanel();
    }

    // The drive under the most pressure (then the least free share) speaks for all; "+N" counts the others under pressure too.
    internal static (string Figure, string BrushKey) Headline(IReadOnlyList<DiskSpaceSnapshot> disks)
    {
        if (disks.Count == 0)
            return ("Disk", "CockpitTextSecondaryBrush");
        var worst = disks.OrderByDescending(disk => disk.Pressure).ThenBy(disk => (double)disk.FreeBytes / Math.Max(1, disk.TotalBytes)).First();
        var eta = _Eta(worst);
        var figure = worst.Pressure switch
        {
            DiskPressure.Critical when eta is not null => $"{worst.Name} {_Bytes(worst.FreeBytes)} · full in {eta}",
            DiskPressure.Elevated when eta is not null => $"{worst.Name} full in {eta}",
            _ => $"{worst.Name} {_Bytes(worst.FreeBytes)}",
        };
        var others = disks.Count(disk => disk != worst && disk.Pressure != DiskPressure.Normal);
        if (others > 0)
            figure += $" +{others}";
        return (figure, _BrushKey(worst.Pressure));
    }

    private static string _BrushKey(DiskPressure pressure) => pressure switch
    {
        DiskPressure.Critical => "CockpitStatusErrorBrush",
        DiskPressure.Elevated => "CockpitStatusWaitingBrush",
        _ => "CockpitTextSecondaryBrush",
    };

    private Control _BuildPanel()
    {
        var close = new Button { Classes = { "Subtle" }, Padding = new Thickness(6, 0), Content = new MaterialIcon { Kind = MaterialIconKind.Close, Width = 12, Height = 12 } };
        ToolTip.SetTip(close, "Close");
        close.Click += (_, _) => _flyout?.Hide();
        var header = new DockPanel();
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        header.Children.Add(new TextBlock { Text = "Disk space", FontWeight = FontWeight.SemiBold, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center });

        _headline = new TextBlock { FontSize = 14, FontWeight = FontWeight.SemiBold };
        _headline.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("CockpitTextPrimaryBrush"));
        _rows = new StackPanel { Spacing = 10 };
        _summary = _Detail("");
        _history = _Detail("");
        _summary.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("CockpitTextFaintBrush"));
        _history.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("CockpitTextFaintBrush"));

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        _cleanup = new Button { Content = "Clean up now" };
        _compact = new Button { Content = "Compact…" };
        _cleanup.Click += async (_, _) => await _RunAsync(() => _maintenance?.CleanupAsync() ?? Task.CompletedTask);
        _compact.Click += async (_, _) => await _RunAsync(() => _maintenance?.RequestCompactionAsync() ?? Task.CompletedTask);
        actions.Children.Add(_cleanup);
        actions.Children.Add(_compact);

        var panel = new StackPanel { Spacing = 10, Width = 320, Margin = new Thickness(14, 12) };
        panel.Children.Add(header);
        panel.Children.Add(_headline);
        panel.Children.Add(_Rule());
        panel.Children.Add(_rows);
        panel.Children.Add(_Rule());
        panel.Children.Add(new TextBlock { Text = "Docker storage", FontSize = 11, FontWeight = FontWeight.SemiBold });
        panel.Children.Add(_summary);
        panel.Children.Add(_history);
        panel.Children.Add(actions);
        _RefreshPanel();
        return new ScrollViewer { Content = panel, MaxHeight = 480, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }

    private Border _Rule()
    {
        var rule = new Border { Height = 1 };
        rule.Bind(Border.BackgroundProperty, this.GetResourceObservable("CockpitHairlineBrush"));
        return rule;
    }

    private void _RefreshPanel()
    {
        if (_headline is null || _rows is null || _summary is null || _history is null || _cleanup is null || _compact is null)
            return;
        var disks = _resources?.Disks ?? [];
        _headline.Text = disks.Count == 0 ? "Awaiting disk readings" : Headline(disks).Figure;
        _rows.Children.Clear();
        foreach (var disk in disks.OrderByDescending(disk => disk.Pressure).ThenBy(disk => disk.Name))
            _rows.Children.Add(_Row(disk));
        _summary.Text = _maintenance?.Summary ?? "Docker storage maintenance is unavailable.";
        _history.Text = string.Join("\n", _maintenance?.History.TakeLast(8) ?? []);
        _history.IsVisible = !string.IsNullOrEmpty(_history.Text);
        _cleanup.IsEnabled = _maintenance is not null && !_busy;
        _compact.IsEnabled = _maintenance?.CanCompact == true && !_busy;
    }

    private Control _Row(DiskSpaceSnapshot disk)
    {
        var free = new TextBlock { Text = $"{_Bytes(disk.FreeBytes)} of {_Bytes(disk.TotalBytes)} free", FontSize = 11.5, FontWeight = FontWeight.SemiBold };
        free.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("CockpitTextPrimaryBrush"));
        DockPanel.SetDock(free, Dock.Right);
        var name = new TextBlock { Text = disk.Name, FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 8, 0) };
        name.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("CockpitTextPrimaryBrush"));
        var top = new DockPanel();
        top.Children.Add(free);
        top.Children.Add(name);

        var pressureKey = disk.Pressure == DiskPressure.Normal ? null : _BrushKey(disk.Pressure);
        var bar = new ProgressBar { Height = 4, Minimum = 0, Maximum = 1, Value = 1 - (double)disk.FreeBytes / Math.Max(1, disk.TotalBytes) };
        bar.Bind(RangeBase.ForegroundProperty, this.GetResourceObservable(pressureKey ?? "CockpitAccentBrush"));
        bar.Bind(TemplatedControl.BackgroundProperty, this.GetResourceObservable("CockpitSecondaryBgBrush"));

        var row = new StackPanel { Spacing = 3, Children = { top, bar } };
        if (_Eta(disk) is { } eta)
        {
            var sub = new TextBlock { Text = $"Full in {eta}", FontSize = 10.5 };
            sub.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable(pressureKey ?? "CockpitTextFaintBrush"));
            row.Children.Add(sub);
        }

        return row;
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

    private static TextBlock _Detail(string text) => new() { Text = text, FontSize = 11, TextWrapping = TextWrapping.Wrap };

    private static string _Bytes(long bytes) => bytes >= 1024L * 1024 * 1024
        ? $"{bytes / (1024d * 1024 * 1024):0.#} GiB" : $"{bytes / (1024d * 1024):0.#} MiB";

    private static string? _Eta(DiskSpaceSnapshot disk)
    {
        if (disk.TimeToFull is not { } eta)
            return null;
        return eta.TotalDays >= 1 ? $"{eta.TotalDays:0.#}d"
            : eta.TotalHours >= 1 ? $"{eta.TotalHours:0.#}h" : $"{Math.Max(1, Math.Ceiling(eta.TotalMinutes))}m";
    }
}
