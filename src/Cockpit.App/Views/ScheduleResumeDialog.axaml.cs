using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Cockpit.App.Controls;
using Cockpit.App.ViewModels;

namespace Cockpit.App.Views;

// Picks a moment and a prompt for a resume scheduled by hand (AC-231).
public partial class ScheduleResumeDialog : Window
{
    public ScheduleResumeDialog()
    {
        InitializeComponent();
        CockpitWindowChrome.Apply(this);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    // A moment already gone would never fire, so the dialog stays open rather than accepting one silently.
    private void OnSchedule(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ScheduleResumeDialogViewModel { IsInTheFuture: true } viewModel)
        {
            Close(viewModel);
        }
    }
}
