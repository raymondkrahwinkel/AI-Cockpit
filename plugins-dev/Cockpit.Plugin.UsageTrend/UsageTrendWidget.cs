using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Cockpit.Plugins.Abstractions.Widgets;

namespace Cockpit.Plugin.UsageTrend;

/// <summary>
/// The usage-trend widget (AC-54): the ctx / 5h / weekly figures the session header shows as now-values, charted
/// over time and split per profile. It samples the active session's usage as the host reports it moving
/// (<c>ICockpitSessionObserver.ActiveSessionUsageChanged</c>), debounced so it does not rewrite the settings file
/// every few seconds, and keeps a rolling fourteen days in its own per-instance storage. With no history yet it
/// shows a plain line about what it is waiting for rather than an empty frame.
/// </summary>
internal sealed class UsageTrendWidget : UserControl
{
    /// <summary>The storage key this instance keeps its sampled history under, within its own slice.</summary>
    internal const string HistoryKey = "history";

    private readonly IWidgetContext _context;
    private readonly StackPanel _profiles = new() { Spacing = 12 };

    private IReadOnlyList<UsageTrendSample> _history;

    public UsageTrendWidget(IWidgetContext context)
    {
        _context = context;

        // What survived a restart, with anything past retention shed before it is ever charted.
        _history = _LoadHistory();

        Content = _BuildLayout();
        _Render();

        // Placing the widget should catch the current reading at once, not only the next time it moves.
        _Sample();

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

    private void _OnUsageChanged(object? sender, EventArgs e) => _Sample();

    private void _OnRefreshRequested(object? sender, EventArgs e)
    {
        // A refresh re-reads the store (another instance of this widget may have appended) and redraws; it never
        // samples, so ↻ cannot forge a data point.
        _history = _LoadHistory();
        _Render();
    }

    // Reads the stored history, defensively. Storage.Get deserializes the raw cockpit.json blob, and a hand-edited or
    // otherwise corrupt one (malformed JSON, or an array with a null element) would throw — out of the widget's
    // construction, which WorkspacesViewModel does while rebuilding every dashboard pane, so one bad blob would cost
    // the operator the whole workspace rather than this one pane. A store it cannot read becomes an empty history; the
    // next sample starts a fresh, valid one. Prune already skips null elements for the same reason.
    private IReadOnlyList<UsageTrendSample> _LoadHistory()
    {
        try
        {
            var stored = _context.Storage.Get<List<UsageTrendSample>>(HistoryKey) ?? [];
            return UsageTrendHistory.Prune(stored, DateTimeOffset.UtcNow);
        }
        catch (Exception)
        {
            return [];
        }
    }

    private void _Sample()
    {
        if (_context.Sessions.ActiveSessionUsage is not { HasAny: true } snapshot)
        {
            return;
        }

        var candidate = UsageTrendSample.From(snapshot, DateTimeOffset.UtcNow);
        var updated = UsageTrendHistory.Append(_history, candidate);
        if (updated is null)
        {
            // Debounced away — the same story a moment later, not worth a whole-file rewrite.
            return;
        }

        _history = updated;
        _context.Storage.Set(HistoryKey, _history);
        _Render();
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
