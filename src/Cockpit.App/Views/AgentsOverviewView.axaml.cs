using Avalonia;
using Avalonia.Controls;

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
        SizeChanged += (_, _) =>
        {
            var count = Bounds.Width < 520 ? 1 : Bounds.Width < 1100 ? 2 : 4;
            ColumnWidth = Math.Max(0, (Bounds.Width - 16) / count);
        };
    }
}
