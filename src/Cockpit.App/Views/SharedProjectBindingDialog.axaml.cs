using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Cockpit.App.Controls;
using Cockpit.App.ViewModels;

namespace Cockpit.App.Views;

/// <summary>
/// The "Finish setting up…" bind step (AC-246). Closes with the new <see cref="Core.Projects.Project"/>, or null
/// when the operator cancelled. Cloning is raised as an event for the host to answer, the same split
/// <see cref="ProjectDialog"/> already uses — the clone flow owns a dialog of its own.
/// </summary>
public partial class SharedProjectBindingDialog : Window
{
    public SharedProjectBindingDialog()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is not SharedProjectBindingDialogViewModel viewModel)
        {
            return;
        }

        Title = viewModel.DialogTitle;
        CockpitWindowChrome.Apply(this, viewModel.DialogTitle, "One koppel-stap: pick a profile, point at a folder if there is one.");

        viewModel.CloseRequested += project => Close(project);
        viewModel.BrowseRequested += () => _ = _BrowseForFolderAsync(viewModel);
    }

    // Mirrors ProjectDialog's own _BrowseForFolderAsync.
    private async Task _BrowseForFolderAsync(SharedProjectBindingDialogViewModel viewModel)
    {
        try
        {
            var start = string.IsNullOrWhiteSpace(viewModel.SourceDirectory)
                ? null
                : await StorageProvider.TryGetFolderFromPathAsync(viewModel.SourceDirectory);

            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select the project's folder",
                AllowMultiple = false,
                SuggestedStartLocation = start,
            });

            var path = folders.FirstOrDefault()?.TryGetLocalPath();
            if (!string.IsNullOrEmpty(path))
            {
                viewModel.ApplyPickedDirectory(path);
            }
        }
        catch
        {
            // Picker unavailable/failed — keep the current value.
        }
    }
}
