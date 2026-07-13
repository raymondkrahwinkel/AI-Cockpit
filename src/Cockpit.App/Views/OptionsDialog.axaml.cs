using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Cockpit.App.Controls;
using Cockpit.App.ViewModels;
using Cockpit.Core.Backup;

namespace Cockpit.App.Views;

/// <summary>
/// Options dialog (#13): a categorised replacement for the sidebar's Options flyout, which had grown
/// too tall for a popup. Its <see cref="Window.DataContext"/> is the shared <c>CockpitViewModel</c>
/// passed in by <see cref="Cockpit.App.Services.SessionDialogService.ShowOptionsDialogAsync"/>. Plugin
/// settings (#14) are no longer top-level tabs — each plugin is configured from the gear next to it in the
/// Plugins tab, which opens the plugin's own settings dialog.
/// </summary>
public partial class OptionsDialog : Window
{
    public OptionsDialog()
    {
        InitializeComponent();
        CockpitWindowChrome.Apply(this);

        // The plugin list is built when the dialog opens rather than when the app started: a plugin installed since
        // then should not be missing from its own backup.
        Opened += (_, _) => (DataContext as CockpitViewModel)?.RefreshBackupPlugins();
    }

    /// <summary>Opens straight to the Plugins tab. Looked up by header text rather than a hardcoded index, so a future tab reorder can't silently select the wrong one.</summary>
    public void SelectPluginsTab()
    {
        foreach (var item in Tabs.Items)
        {
            if (item is TabItem { Header: "Plugins" })
            {
                Tabs.SelectedItem = item;
                return;
            }
        }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    // The file pickers live here because picking a file is a view's job (Window.StorageProvider), the same way the
    // profile dialog picks a directory. What goes *in* the archive is the view model's business, not this one's.
    private async void OnCheckForUpdates(object? sender, RoutedEventArgs e)
    {
        if (DataContext is CockpitViewModel cockpit)
        {
            await cockpit.CheckForUpdatesAsync();
        }
    }

    private void OnOpenUpdate(object? sender, RoutedEventArgs e) =>
        (DataContext as CockpitViewModel)?.OpenUpdate();

    private async void OnCreateBackup(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not CockpitViewModel cockpit)
        {
            return;
        }

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Back up this cockpit",
            SuggestedFileName = $"cockpit-backup-{DateTime.Now:yyyy-MM-dd}.zip",
            DefaultExtension = "zip",
            FileTypeChoices = [new FilePickerFileType("Cockpit backup") { Patterns = ["*.zip"] }],
        });

        if (file?.TryGetLocalPath() is { Length: > 0 } path)
        {
            await cockpit.CreateBackupAsync(path);
        }
    }

    private async void OnRestoreBackup(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not CockpitViewModel cockpit)
        {
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Restore from a backup",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Cockpit backup") { Patterns = ["*.zip"] }],
        });

        if (files.FirstOrDefault()?.TryGetLocalPath() is { Length: > 0 } path)
        {
            // The archive says what it carries; the operator says what of it comes back. Reading first and asking
            // second is the whole difference between a restore and a surprise.
            await cockpit.RestoreBackupAsync(path, async manifest =>
            {
                var dialog = new RestoreSelectionDialog
                {
                    DataContext = new RestoreSelectionViewModel(manifest, cockpit.InstalledPluginIds),
                };

                return await dialog.ShowDialog<RestoreOptions?>(this);
            });
        }
    }
}
