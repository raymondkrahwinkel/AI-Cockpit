using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Cockpit.App.ViewModels;

namespace Cockpit.App.Views;

public partial class ProfilesEditor : UserControl
{
    public ProfilesEditor()
    {
        InitializeComponent();
    }

    // Browse buttons (AC-1001), shown only where the profiles' paths are on this machine (AC-1585).
    private async void OnBrowseProfileConfigDir(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ManageProfilesDialogViewModel { SelectedProfile: { } profile } || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return;
        }

        try
        {
            var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
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

    private async void OnBrowseProfileWorkingDirectory(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ManageProfilesDialogViewModel { SelectedProfile: { } profile } || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return;
        }

        try
        {
            var start = string.IsNullOrWhiteSpace(profile.DefaultWorkingDirectory)
                ? null
                : await storage.TryGetFolderFromPathAsync(profile.DefaultWorkingDirectory);

            var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
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

    private async void OnBrowseProfileExecutable(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ManageProfilesDialogViewModel { SelectedProfile: { } profile } || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return;
        }

        try
        {
            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
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
