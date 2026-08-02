using Avalonia.Controls;
using Avalonia.Interactivity;
using Cockpit.App.Controls;
using Cockpit.App.ViewModels;

namespace Cockpit.App.Views;

// Asks for a password. Returns the view model (with what was typed) from `ShowDialog&lt;PasswordDialogViewModel?&gt;`
// when the operator confirms, and `null` when they cancel.
public partial class PasswordDialog : Window
{
    public PasswordDialog()
    {
        InitializeComponent();

        DataContextChanged += (_, _) =>
        {
            if (DataContext is PasswordDialogViewModel viewModel)
            {
                Title = viewModel.Title;
                CockpitWindowChrome.Apply(this, viewModel.Title);
            }
        };

        Opened += (_, _) => this.FindControl<TextBox>("NewPasswordBox")?.Focus();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private void OnConfirm(object? sender, RoutedEventArgs e) => Close(DataContext as PasswordDialogViewModel);
}
