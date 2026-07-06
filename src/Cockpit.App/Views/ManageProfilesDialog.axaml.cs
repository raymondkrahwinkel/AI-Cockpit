using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
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
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is ManageProfilesDialogViewModel viewModel)
        {
            viewModel.CloseRequested += () => Close();
        }
    }

    private async void OnBrowseConfigDir(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ManageProfilesDialogViewModel { SelectedProfile: { } profile })
        {
            return;
        }

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

    private async void OnBrowseExecutable(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ManageProfilesDialogViewModel { SelectedProfile: { } profile })
        {
            return;
        }

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
}
