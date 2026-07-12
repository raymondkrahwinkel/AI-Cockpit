using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
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

    // Opens the OS folder picker and drops the chosen directory into the working-directory field. The picker
    // needs the window's TopLevel, so it lives here rather than in the view model; the VM owns everything else.
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
