using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Cockpit.App.ViewModels;
using Cockpit.Core.Projects;

namespace Cockpit.App.Views;

/// <summary>
/// Creating or editing one project (AC-160). Closes with the edited <see cref="Project"/>, or null when the
/// operator cancelled. The folder picker lives here because it needs the window; cloning is raised as an event
/// for the host to answer, since the clone flow owns a dialog of its own.
/// </summary>
public partial class ProjectDialog : Window
{
    public ProjectDialog()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is not ProjectDialogViewModel viewModel)
        {
            return;
        }

        viewModel.CloseRequested += project => Close(project);
        viewModel.BrowseRequested += () => _ = _BrowseForFolderAsync(viewModel);
    }

    // Pre-seeded with the current value so re-browsing opens where the project already points. A failed or
    // cancelled pick keeps what is there.
    private async Task _BrowseForFolderAsync(ProjectDialogViewModel viewModel)
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
