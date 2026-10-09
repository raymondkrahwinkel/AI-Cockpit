using Avalonia.Controls;
using Cockpit.App.ViewModels;

namespace Cockpit.App.Views;

public partial class UsageThresholdGroup : UserControl
{
    public UsageThresholdGroup()
    {
        InitializeComponent();
    }

    public UsageThresholdGroup(UsageThresholdProviderViewModel group)
        : this()
    {
        DataContext = group;
        Rows.Children.AddRange(group.Signals.Select(signal => new UsageThresholdRow { DataContext = signal }));
    }
}
