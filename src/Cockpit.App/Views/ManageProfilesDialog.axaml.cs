using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Cockpit.App.Controls;
using Cockpit.App.ViewModels;

namespace Cockpit.App.Views;

/// <summary>
/// Manage-profiles dialog. Closes when the view model raises
/// <see cref="ManageProfilesDialogViewModel.CloseRequested"/>. The Browse buttons use the window's
/// <see cref="Window.StorageProvider"/> (a view-layer facility) to fill the selected profile's config
/// directory and executable path.
/// </summary>
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

    // async void: these are UI event handlers, so an unobserved exception from the picker (no desktop
    // portal, permission denied) would tear down the process. A failed/cancelled pick is non-fatal —
    // the operator just keeps the current path — so swallow it here rather than crash the app.
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
