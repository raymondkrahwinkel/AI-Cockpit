using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Cockpit.App.Controls;
using Cockpit.App.ViewModels;

namespace Cockpit.App.Views;

/// <summary>
/// The assistant's own profile editor. Closes when the view model raises
/// <see cref="AssistantProfileDialogViewModel.CloseRequested"/>; the Browse buttons use the window's
/// <see cref="Window.StorageProvider"/>, a view-layer facility, the same split the Manage-profiles dialog uses.
/// </summary>
public partial class AssistantProfileDialog : Window
{
    public AssistantProfileDialog()
    {
        InitializeComponent();
        CockpitWindowChrome.Apply(this);
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is AssistantProfileDialogViewModel viewModel)
        {
            viewModel.CloseRequested += () => Close();
        }
    }

    // async void: a UI event handler, so an unobserved exception from the picker (no desktop portal, permission
    // denied) would tear down the process. A failed or cancelled pick is non-fatal — the current path stands.
    private async void OnBrowseConfigDir(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AssistantProfileDialogViewModel { Profile: { } profile })
        {
            return;
        }

        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select the assistant profile's config directory",
                AllowMultiple = false,
            });

            if (folders.FirstOrDefault()?.TryGetLocalPath() is { Length: > 0 } path)
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
        if (DataContext is not AssistantProfileDialogViewModel { Profile: { } profile })
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

            if (files.FirstOrDefault()?.TryGetLocalPath() is { Length: > 0 } path)
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
