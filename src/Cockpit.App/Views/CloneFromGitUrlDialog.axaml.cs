using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Cockpit.App.Controls;
using Cockpit.App.ViewModels;

namespace Cockpit.App.Views;

// AC-90: clones a repository from a URL, returning the local clone path or null on cancel, so
// the New-session dialog only adopts a working directory that actually exists.
public partial class CloneFromGitUrlDialog : Window
{
    public CloneFromGitUrlDialog()
    {
        InitializeComponent();
        // The title is this dialog's own, not the view model's, so the chrome goes on here rather than waiting for
        // a data context that only ever changes what the dialog does — not what its title bar says.
        CockpitWindowChrome.Apply(this, "Clone from a Git URL");

        DataContextChanged += (_, _) =>
        {
            if (DataContext is CloneFromGitUrlDialogViewModel viewModel)
            {
                viewModel.CloseRequested += path => Close(path);
            }
        };

        Opened += (_, _) => this.FindControl<TextBox>("UrlBox")?.Focus();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    // Git clones into a new/empty folder, so the picked folder is treated as the *parent* and the
    // repo's own folder name kept underneath — falls back to the picked folder when there's no name yet.
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
