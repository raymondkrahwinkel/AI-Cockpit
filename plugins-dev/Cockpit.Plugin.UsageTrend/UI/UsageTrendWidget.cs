using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Cockpit.Plugin.UsageTrend.Contracts;
using Cockpit.Plugins.Abstractions.UI;
using Cockpit.Plugins.Abstractions.Widgets;

namespace Cockpit.Plugin.UsageTrend.UI;

// The usage-trend widget (AC-54): ctx / 5h / weekly, charted over time per profile. AC-1395 split: the debounce/
// prune rules and the cache file live in the backend part (UsageTrendPlugin), reached over the plugin's channel
// — ICockpitUiHost does not expose ICockpitHost.Cache.
internal sealed class UsageTrendWidget : UserControl
{
    // The storage key a pre-AC-1395 install left this instance's history under, migrated once then removed.
    internal const string HistoryKey = "history";

    private readonly IWidgetContext _context;
    private readonly IPluginUiChannel _channel;
    private readonly StackPanel _profiles = new() { Spacing = 12 };

    private IReadOnlyList<UsageTrendSample> _history = [];

    public UsageTrendWidget(IWidgetContext context, IPluginUiChannel channel)
    {
        _context = context;
        _channel = channel;

        Content = _BuildLayout();
        _Render();

        // Placing the widget should catch the current reading at once, not only the next time it moves.
        _ = _InitializeAsync();

        _context.Sessions.ActiveSessionUsageChanged += _OnUsageChanged;
        _context.RefreshRequested += _OnRefreshRequested;
        DetachedFromVisualTree += (_, _) =>
        {
            _context.Sessions.ActiveSessionUsageChanged -= _OnUsageChanged;
            _context.RefreshRequested -= _OnRefreshRequested;
        };
    }

    private Control _BuildLayout()
    {
        var root = new DockPanel { LastChildFill = true };

        var legend = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 14,
            Margin = new Thickness(4, 0, 4, 8),
            [DockPanel.DockProperty] = Dock.Top,
            Children =
            {
                _LegendEntry("Context", UsageTrendChartControl.ContextColor),
                _LegendEntry("5h", UsageTrendChartControl.FiveHourColor),
                _LegendEntry("Week", UsageTrendChartControl.WeeklyColor),
            },
        };

        root.Children.Add(legend);
        root.Children.Add(new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = _profiles,
        });

        return root;
    }

    // Migrates a pre-AC-1395 install's legacy per-instance storage into the backend's cache (once, only when
    // that cache is still empty — the backend enforces the guard), then loads and takes the first sample.
    private async Task _InitializeAsync()
    {
        if (_context.Storage.Get<List<UsageTrendSample>>(HistoryKey) is { } legacy)
        {
            await _SeedAsync(legacy);
        }

        _context.Storage.Remove(HistoryKey);

        _history = await _LoadAsync();
        _Render();
        await _SampleAsync();
    }

    private async void _OnUsageChanged(object? sender, EventArgs e) => await _SampleAsync();

    private async void _OnRefreshRequested(object? sender, EventArgs e)
    {
        // A refresh re-reads the store (another instance of this widget may have appended) and redraws; it never
        // samples, so ↻ cannot forge a data point.
        _history = await _LoadAsync();
        _Render();
    }

    // Reads the backend's cache-backed history for this instance. A failed round trip (backend not yet up,
    // corrupt cache) leaves the widget on an empty history rather than throwing out of an event handler.
    private async Task<IReadOnlyList<UsageTrendSample>> _LoadAsync()
    {
        try
        {
            var payload = JsonSerializer.SerializeToElement(new UsageTrendHistoryRequest(_context.InstanceId), UsageTrendChannel.Json);
            var answer = await _channel.InvokeAsync(UsageTrendChannel.Get, payload);
            return answer.Deserialize<List<UsageTrendSample>>(UsageTrendChannel.Json) ?? [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    private async Task _SeedAsync(IReadOnlyList<UsageTrendSample> history)
    {
        try
        {
            var payload = JsonSerializer.SerializeToElement(new UsageTrendSeedRequest(_context.InstanceId, history), UsageTrendChannel.Json);
            await _channel.InvokeAsync(UsageTrendChannel.Seed, payload);
        }
        catch (Exception)
        {
        }
    }

    private async Task _SampleAsync()
    {
        if (_context.Sessions.ActiveSessionUsage is not { HasAny: true } snapshot)
        {
            return;
        }

        var candidate = UsageTrendSample.From(snapshot, DateTimeOffset.UtcNow);
        try
        {
            var payload = JsonSerializer.SerializeToElement(new UsageTrendAppendRequest(_context.InstanceId, candidate), UsageTrendChannel.Json);
            var answer = await _channel.InvokeAsync(UsageTrendChannel.Append, payload);
            _history = answer.Deserialize<List<UsageTrendSample>>(UsageTrendChannel.Json) ?? _history;
            _Render();
        }
        catch (Exception)
        {
        }
    }

    private void _Render()
    {
        _profiles.Children.Clear();

        if (_history.Count == 0)
        {
            _profiles.Children.Add(new TextBlock
            {
                Text = "No usage recorded yet. A trend appears here as your sessions report context and rate-limit usage.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.7,
                FontSize = 12,
                Margin = new Thickness(4),
            });

            return;
        }

        // One section per profile, in a stable alphabetical order so the layout does not reshuffle between renders.
        var groups = _history
            .GroupBy(sample => sample.ProfileLabel)
            .OrderBy(group => group.Key ?? string.Empty, StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups)
        {
            _profiles.Children.Add(_ProfileSection(group.Key, [.. group]));
        }
    }

    private static Control _ProfileSection(string? profileLabel, IReadOnlyList<UsageTrendSample> samples)
    {
        var header = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(profileLabel) ? "Unknown profile" : profileLabel,
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(4, 0, 4, 2),
        };

        var chart = new UsageTrendChartControl
        {
            Samples = samples,
            Height = 96,
            MinHeight = 96,
        };

        return new StackPanel { Spacing = 2, Children = { header, chart } };
    }

    private static Control _LegendEntry(string label, Color color) => new StackPanel
    {
        Orientation = Orientation.Horizontal,
        Spacing = 5,
        Children =
        {
            new Border
            {
                Width = 10,
                Height = 10,
                CornerRadius = new CornerRadius(2),
                Background = new SolidColorBrush(color),
                VerticalAlignment = VerticalAlignment.Center,
            },
            new TextBlock { Text = label, FontSize = 11, Opacity = 0.8, VerticalAlignment = VerticalAlignment.Center },
        },
    };
}
