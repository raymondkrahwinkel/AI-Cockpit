using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
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

    // Browse for the destination. A folder picker returns an existing folder, but git clones into a new (or empty)
    // one — so the picked folder is treated as the *parent* and the repository's own folder name is kept underneath
    // it, which is also what makes "relocate to another disk, same layout" a one-click move. Falls back to the picked
    // folder itself when there is no name to keep yet.
    private async void OnBrowseTarget(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (DataContext is not CloneFromGitUrlDialogViewModel viewModel)
            {
                return;
            }

            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Choose where to clone",
                AllowMultiple = false,
            });

            if (folders is { Count: > 0 } && folders[0].TryGetLocalPath() is { Length: > 0 } picked)
            {
                var leaf = Path.GetFileName(viewModel.TargetFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                viewModel.TargetFolder = string.IsNullOrEmpty(leaf) ? picked : Path.Combine(picked, leaf);
            }
        }
        catch (Exception)
        {
            // Picking a folder is best-effort: a picker that fails to open must not tear the dialog down. The
            // operator can still type the path.
        }
    }

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
