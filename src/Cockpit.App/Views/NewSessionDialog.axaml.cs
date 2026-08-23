using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Cockpit.App.Controls;
using Cockpit.App.ViewModels;

namespace Cockpit.App.Views;

// AC-367: New-session form, opened beside the cockpit. Not shown with ShowDialog, so
// SessionDialogService must subscribe to CloseRequested before this window's DataContext is
// set, since the handler below (on DataContextChanged) closes the window first.
public partial class NewSessionDialog : Window
{
    public NewSessionDialog()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (DataContext is NewSessionDialogViewModel viewModel)
        {
            // Title bar waits for the view model — "New session" vs "Continue session" depends on
            // it, and applying it in the constructor showed Avalonia's default "Window" instead.
            CockpitWindowChrome.Apply(this, viewModel.HeaderText);
            viewModel.CloseRequested += result => Close(result);
        }
    }

    // Needs the window's TopLevel, so the picker lives here rather than in the view model.
    private async void OnBrowseWorkingDirectory(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not NewSessionDialogViewModel viewModel)
        {
            return;
        }

        var start = string.IsNullOrWhiteSpace(viewModel.WorkingDirectory)
            ? null
            : await StorageProvider.TryGetFolderFromPathAsync(viewModel.WorkingDirectory);

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose a working directory",
            AllowMultiple = false,
            SuggestedStartLocation = start,
        });

        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
        {
            viewModel.WorkingDirectory = path;
        }
    }
}
