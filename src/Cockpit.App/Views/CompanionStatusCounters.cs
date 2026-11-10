using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.VisualTree;
using Cockpit.App.Services;

namespace Cockpit.App.Views;

internal sealed class CompanionStatusCounters : StackPanel
{
    private readonly CompanionAttentionFeed _feed;

    public CompanionStatusCounters(CompanionAttentionFeed feed)
    {
        _feed = feed;
        Orientation = Orientation.Horizontal;
        _Refresh();
        _Fit();
    }

    // AC-2190: the mockup's status item, 8px in from either side across a horizontal strip and 5px down a vertical one.
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == OrientationProperty)
        {
            _Fit();
        }
    }

    private void _Fit()
    {
        var horizontal = Orientation == Orientation.Horizontal;
        Spacing = horizontal ? 8 : 2;
        Margin = horizontal ? new Thickness(8, 0) : new Thickness(0, 5);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (this.GetVisualAncestors().OfType<StackPanel>().FirstOrDefault(panel => panel.Name == "ToolsHost") is { } host)
        {
            Bind(OrientationProperty, new Binding(nameof(Orientation)) { Source = host });
        }
        _feed.StatusChanged += _Refresh;
        _Refresh();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _feed.StatusChanged -= _Refresh;
        base.OnDetachedFromVisualTree(e);
    }

    private void _Refresh()
    {
        Children.Clear();
        _Add(_feed.RunningCount, "Busy", "Running");
        _Add(_feed.WaitingCount, "Waiting", "Waiting for you");
        _Add(_feed.BackgroundCount, "Background", "Background");
        if (Children.Count == 0)
        {
            Children.Add(_Dot("CockpitTextFaintBrush"));
        }
    }

    private void _Add(int count, string tone, string name)
    {
        if (count == 0)
        {
            return;
        }
        var pair = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        pair.Children.Add(_Dot($"CockpitStatus{tone}Brush"));
        var number = new TextBlock { Text = count.ToString(System.Globalization.CultureInfo.InvariantCulture), FontSize = 12, FontWeight = Avalonia.Media.FontWeight.SemiBold };
        number.Bind(TextBlock.ForegroundProperty, number.GetResourceObservable("CockpitTextPrimaryBrush"));
        pair.Children.Add(number);
        ToolTip.SetTip(pair, $"{name}: {count}");
        Children.Add(pair);
    }

    private static Ellipse _Dot(string brush)
    {
        var dot = new Ellipse { Width = 7, Height = 7, VerticalAlignment = VerticalAlignment.Center };
        dot.Bind(Shape.FillProperty, dot.GetResourceObservable(brush));
        return dot;
    }
}
