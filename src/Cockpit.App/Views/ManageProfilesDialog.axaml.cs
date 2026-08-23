using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Cockpit.App.Controls;
using Cockpit.App.ViewModels;

namespace Cockpit.App.Views;

// Manage-profiles dialog. Closes on CloseRequested; the Browse buttons use the window's
// StorageProvider (a view-layer facility) to fill the selected profile's config dir/executable.
public partial class ManageProfilesDialog : Window
{
    public ManageProfilesDialog()
    {
        InitializeComponent();
        CockpitWindowChrome.Apply(this);
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is ManageProfilesDialogViewModel viewModel)
        {
            viewModel.CloseRequested += () => Close();
        }
    }

    // async void handlers: an unobserved picker exception (no portal, permission denied) would tear
    // down the process, so a failed/cancelled pick is swallowed and just keeps the current path.
    private async void OnBrowseConfigDir(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ManageProfilesDialogViewModel { SelectedProfile: { } profile })
        {
            return;
        }

        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select the profile's config directory",
                AllowMultiple = false,
            });

            var path = folders.FirstOrDefault()?.TryGetLocalPath();
            if (!string.IsNullOrEmpty(path))
            {
                profile.ConfigDir = path;
            }
        }
        catch
        {
            // Picker unavailable/failed — keep the current value.
        }
    }

    // AC-130: same folder picker as the config directory, pre-seeded with the current value.
    private async void OnBrowseWorkingDirectory(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ManageProfilesDialogViewModel { SelectedProfile: { } profile })
        {
            return;
        }

        try
        {
            var start = string.IsNullOrWhiteSpace(profile.DefaultWorkingDirectory)
                ? null
                : await StorageProvider.TryGetFolderFromPathAsync(profile.DefaultWorkingDirectory);

            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select the profile's default working directory",
                AllowMultiple = false,
                SuggestedStartLocation = start,
            });

            var path = folders.FirstOrDefault()?.TryGetLocalPath();
            if (!string.IsNullOrEmpty(path))
            {
                profile.DefaultWorkingDirectory = path;
            }
        }
        catch
        {
            // Picker unavailable/failed — keep the current value.
        }
    }

    private async void OnBrowseExecutable(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ManageProfilesDialogViewModel { SelectedProfile: { } profile })
        {
            return;
        }

        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select the claude executable",
                AllowMultiple = false,
            });

            var path = files.FirstOrDefault()?.TryGetLocalPath();
            if (!string.IsNullOrEmpty(path))
            {
                profile.ExecutablePath = path;
            }
        }
        catch
        {
            // Picker unavailable/failed — keep the current value.
        }
    }
}
