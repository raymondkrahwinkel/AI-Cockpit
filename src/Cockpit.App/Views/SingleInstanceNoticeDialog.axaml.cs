using Avalonia.Controls;
using Avalonia.Interactivity;
using Cockpit.App.Controls;

namespace Cockpit.App.Views;

/// <summary>
/// Tells the operator that a second cockpit will not start, and why (AC-4). It is shown before the app exists —
/// there is no cockpit behind it and no owner to centre on, because the whole point is that this process is about
/// to stop.
/// </summary>
public partial class SingleInstanceNoticeDialog : Window
{
    public SingleInstanceNoticeDialog()
    {
        InitializeComponent();
        CockpitWindowChrome.Apply(this, Title!);
    }

    private void OnDismiss(object? sender, RoutedEventArgs e) => Close();
}
