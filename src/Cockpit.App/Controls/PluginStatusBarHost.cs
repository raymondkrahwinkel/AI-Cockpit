using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Cockpit.App.ViewModels;
using Cockpit.Plugins.Abstractions.StatusBar;

namespace Cockpit.App.Controls;

// AC-82/AC-1013: renders plugin-registered supervised-activity sources in the status bar, one counter
// button per source with a flyout listing activities and a Kill button. Kill is operator-only — it calls
// `SupervisedActivity.StopAsync` directly; an agent has no path to it.
internal sealed class PluginStatusBarHost : StackPanel
{
    private readonly List<SourceEntry> _entries = [];
    private CockpitViewModel? _cockpit;

    public PluginStatusBarHost()
    {
        Orientation = Orientation.Horizontal;
        VerticalAlignment = VerticalAlignment.Center;

        AttachedToVisualTree += (_, _) => _Build();
        DetachedFromVisualTree += (_, _) => _Clear();
    }

    private void _Build()
    {
        _Clear();

        _cockpit = Program.Services?.GetService<CockpitViewModel>();
        if (_cockpit is null)
        {
            return;
        }

        _cockpit.PluginSupervisedActivities.CollectionChanged += _OnSourcesChanged;
        foreach (var source in _cockpit.PluginSupervisedActivities)
        {
            _Add(source);
        }
    }

    // A plugin can register a source after this host attached (plugins initialise at startup, but be robust to it).
    private void _OnSourcesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _ClearEntries();
        if (_cockpit is null)
        {
            return;
        }

        foreach (var source in _cockpit.PluginSupervisedActivities)
        {
            _Add(source);
        }
    }

    private void _Add(ISupervisedActivitySource source)
    {
        var text = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        var button = new Button
        {
            Classes = { "statusMeter" },
            Padding = new Thickness(8, 2),
            Margin = new Thickness(0, 0, 8, 0),
            Content = text,
        };
        button.Click += (_, _) => _ShowPanel(source, button);

        void OnChanged() => Dispatcher.UIThread.Post(() => _Refresh(source, text, button));
        source.Changed += OnChanged;

        _entries.Add(new SourceEntry(source, button, OnChanged));
        Children.Add(button);
        _Refresh(source, text, button);
    }

    private static void _Refresh(ISupervisedActivitySource source, TextBlock text, Button button)
    {
        var count = source.Snapshot().Count;
        text.Text = $"{source.Label}: {count}";
        // Only visible while something is running — no dead "Port-forwards: 0" clutter.
        button.IsVisible = count > 0;
    }

    private static void _ShowPanel(ISupervisedActivitySource source, Button button)
    {
        var flyout = new Flyout { Placement = PlacementMode.Top };
        flyout.Content = _BuildPanel(source, flyout);
        flyout.ShowAt(button);
    }

    private static Control _BuildPanel(ISupervisedActivitySource source, Flyout flyout)
    {
        var panel = new StackPanel { Spacing = 6, MinWidth = 300, Margin = new Thickness(4) };
        panel.Children.Add(new TextBlock { Text = source.Label, FontWeight = FontWeight.SemiBold });

        var activities = source.Snapshot();
        if (activities.Count == 0)
        {
            panel.Children.Add(new TextBlock { Text = "Nothing running.", FontSize = 11, Opacity = 0.7 });
        }

        foreach (var activity in activities)
        {
            panel.Children.Add(_BuildRow(activity, flyout));
        }

        return panel;
    }

    private static Control _BuildRow(SupervisedActivity activity, Flyout flyout)
    {
        var info = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(new TextBlock { Text = activity.Title, FontSize = 12 });
        foreach (var detail in activity.Details)
        {
            info.Children.Add(new TextBlock { Text = $"{detail.Label}: {detail.Value}", FontSize = 11, Opacity = 0.7 });
        }

        var kill = new Button { Content = "Kill", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        kill.Click += async (_, _) =>
        {
            kill.IsEnabled = false;
            try
            {
                await activity.StopAsync();
            }
            catch (Exception exception)
            {
                // Fail-soft: the source's own Changed event reconciles the counter; a failed stop should not crash the UI.
                Program.Services?.GetService<ILoggerFactory>()?.CreateLogger<PluginStatusBarHost>()
                    .LogWarning(exception, "Could not stop supervised activity '{ActivityId}'.", activity.Id);
            }

            flyout.Hide();
        };

        var row = new DockPanel();
        DockPanel.SetDock(kill, Dock.Right);
        row.Children.Add(kill);
        row.Children.Add(info);
        return new Border { Padding = new Thickness(0, 4), Child = row };
    }

    private void _Clear()
    {
        if (_cockpit is not null)
        {
            _cockpit.PluginSupervisedActivities.CollectionChanged -= _OnSourcesChanged;
        }

        _ClearEntries();
        _cockpit = null;
    }

    private void _ClearEntries()
    {
        foreach (var entry in _entries)
        {
            entry.Source.Changed -= entry.OnChanged;
        }

        _entries.Clear();
        Children.Clear();
    }

    private sealed record SourceEntry(ISupervisedActivitySource Source, Button Button, Action OnChanged);
}
