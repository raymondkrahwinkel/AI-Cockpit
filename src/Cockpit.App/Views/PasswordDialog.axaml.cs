using Avalonia.Controls;
using Avalonia.Interactivity;
using Cockpit.App.Controls;
using Cockpit.App.ViewModels;

namespace Cockpit.App.Views;

/// <summary>
/// Asks for a password. Returns the view model (with what was typed) from <c>ShowDialog&lt;PasswordDialogViewModel?&gt;</c>
/// when the operator confirms, and <see langword="null"/> when they cancel.
/// </summary>
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
