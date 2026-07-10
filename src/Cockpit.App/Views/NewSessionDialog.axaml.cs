using Avalonia.Controls;
using Cockpit.App.Controls;
using Cockpit.App.ViewModels;

namespace Cockpit.App.Views;

/// <summary>
/// Modal New-session dialog. Closes with the confirmed <see cref="NewSessionResult"/> (or null on
/// cancel) when the view model raises <see cref="NewSessionDialogViewModel.CloseRequested"/>, so the
/// caller gets the result straight from <c>ShowDialog&lt;NewSessionResult?&gt;</c>.
/// </summary>
public partial class NewSessionDialog : Window
{
    public NewSessionDialog()
    {
        InitializeComponent();
        CockpitWindowChrome.Apply(this);
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (DataContext is NewSessionDialogViewModel viewModel)
        {
            viewModel.CloseRequested += result => Close(result);
        }
    }
}
