using Avalonia.Controls;
using Avalonia.Interactivity;
using Cockpit.App.Controls;
using Cockpit.App.ViewModels;

namespace Cockpit.App.Views;

// Generic confirmation dialog for destructive actions. Returns `true` from `ShowDialog&lt;bool&gt;`
// only when the operator clicks confirm; Cancel, the ✕, or Esc all return `false`.
public partial class ConfirmationDialog : Window
{
    public ConfirmationDialog()
    {
        InitializeComponent();
        // Apply the chrome once the title is known (it comes from the view model), so the title bar reads e.g.
        // "Remove store" rather than the placeholder.
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ConfirmationDialogViewModel viewModel)
            {
                Title = viewModel.Title;
                CockpitWindowChrome.Apply(this, viewModel.Title);
            }
        };
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    private void OnConfirm(object? sender, RoutedEventArgs e) => Close(true);
}
