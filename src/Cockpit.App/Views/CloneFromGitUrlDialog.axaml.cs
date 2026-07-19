using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Cockpit.App.Controls;
using Cockpit.App.ViewModels;

namespace Cockpit.App.Views;

/// <summary>
/// Clones a repository from a URL (AC-90). Returns the local clone path from <c>ShowDialog&lt;string?&gt;</c> when the
/// clone succeeds, and <see langword="null"/> when the operator cancels, so the New-session dialog only adopts a
/// working directory that is actually there. The clone itself runs in the view model, which raises
/// <see cref="CloneFromGitUrlDialogViewModel.CloseRequested"/> with the path once it lands.
/// </summary>
public partial class CloneFromGitUrlDialog : Window
{
    public CloneFromGitUrlDialog()
    {
        InitializeComponent();

        DataContextChanged += (_, _) =>
        {
            if (DataContext is CloneFromGitUrlDialogViewModel viewModel)
            {
                CockpitWindowChrome.Apply(this, "Clone from a Git URL");
                viewModel.CloseRequested += path => Close(path);
            }
        };

        Opened += (_, _) => this.FindControl<TextBox>("UrlBox")?.Focus();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private void OnUrlBoxKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                if (DataContext is CloneFromGitUrlDialogViewModel viewModel && viewModel.CloneCommand.CanExecute(null))
                {
                    viewModel.CloneCommand.Execute(null);
                    e.Handled = true;
                }

                break;
            case Key.Escape:
                // Handle it here so the window chrome's own bubbling Escape-to-close doesn't fire a second Close.
                Close(null);
                e.Handled = true;
                break;
        }
    }
}
