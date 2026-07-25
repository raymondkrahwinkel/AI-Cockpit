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
        viewModel.PickLogoRequested += () => _ = _PickLogoAsync(viewModel);
        viewModel.PickMemoryRequested += () => _ = _PickMemoryFolderAsync(viewModel);
    }

    // Deliberately a folder picker and not the source-folder one: memory is somewhere else by definition, and
    // seeding this picker at the project's own folder would suggest otherwise.
    private async Task _PickMemoryFolderAsync(ProjectDialogViewModel viewModel)
    {
        try
        {
            var start = string.IsNullOrWhiteSpace(viewModel.MemoryRef)
                ? null
                : await StorageProvider.TryGetFolderFromPathAsync(viewModel.MemoryRef);

            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select where this project's memory lives",
                AllowMultiple = false,
                SuggestedStartLocation = start,
            });

            if (folders.FirstOrDefault()?.TryGetLocalPath() is { Length: > 0 } path)
            {
                viewModel.MemoryRef = path;
            }
        }
        catch
        {
            // No picker here either — the field takes a typed path, or a reference a plugin understands.
        }
    }

    // The picked file's path lands in LogoSource; the manager takes the copy when the project is saved, so a
    // cancelled dialog leaves the operator's pictures and the stored one alone.
    private async Task _PickLogoAsync(ProjectDialogViewModel viewModel)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Choose the project's logo",
                AllowMultiple = false,
                FileTypeFilter = [FilePickerFileTypes.ImageAll],
            });

            if (files.FirstOrDefault()?.TryGetLocalPath() is { Length: > 0 } path)
            {
                viewModel.LogoSource = path;
            }
        }
        catch
        {
            // No picker on this platform, or the operator's file manager refused. The field takes a path or a URL
            // typed by hand, so the flow is not lost.
        }
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
