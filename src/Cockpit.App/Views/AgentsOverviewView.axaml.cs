using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Cockpit.App.ViewModels;

namespace Cockpit.App.Views;

public partial class AgentsOverviewView : UserControl
{
    public static readonly StyledProperty<double> ColumnWidthProperty =
        AvaloniaProperty.Register<AgentsOverviewView, double>(nameof(ColumnWidth));

    public double ColumnWidth
    {
        get => GetValue(ColumnWidthProperty);
        set => SetValue(ColumnWidthProperty, value);
    }

    public AgentsOverviewView()
    {
        InitializeComponent();
        var refresh = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        refresh.Tick += (_, _) => (DataContext as AgentsOverviewViewModel)?.RefreshCards();
        AttachedToVisualTree += (_, _) => refresh.Start();
        DetachedFromVisualTree += (_, _) => refresh.Stop();
        SizeChanged += (_, _) =>
        {
            var count = Bounds.Width < 520 ? 1 : Bounds.Width < 1100 ? 2 : 4;
            ColumnWidth = Math.Max(0, (Bounds.Width - 16) / count);
        };
    }
}
