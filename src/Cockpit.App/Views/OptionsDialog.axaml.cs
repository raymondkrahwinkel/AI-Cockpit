using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Cockpit.App.Controls;
using Cockpit.App.ViewModels;
using Cockpit.Core.Backup;
using Cockpit.Core.Configuration;

namespace Cockpit.App.Views;

// Options dialog (#13): a categorised replacement for the sidebar's Options flyout, which had grown
// too tall for a popup. Its `Window.DataContext` is the shared `CockpitViewModel`
// passed in by `Cockpit.App.Services.SessionDialogService.ShowOptionsDialogAsync`. Plugin
// settings (#14) are no longer top-level tabs — each plugin is configured from the gear next to it in the
// Plugins tab, which opens the plugin's own settings dialog.
public partial class OptionsDialog : Window
{
    public OptionsDialog()
    {
        InitializeComponent();
        CockpitWindowChrome.Apply(this);

        // The plugin list is built when the dialog opens rather than when the app started: a plugin installed since
        // then should not be missing from its own backup. The diagnostics panel is read the same way — once, on
        // open — so the Debug tab shows current figures without a timer running behind a page nobody is looking at.
        Opened += (_, _) =>
        {
            if (DataContext is CockpitViewModel cockpit)
            {
                cockpit.RefreshBackupPlugins();
                cockpit.Diagnostics.Refresh();
            }
        };

        // However the dialog closes (Apply, Cancel, the window chrome, Escape), release the microphone if a level
        // test was left running.
        Closed += (_, _) => (DataContext as CockpitViewModel)?.StopMicTest();
        Closing += OnClosingDialog;
    }

    // Set by the two paths that have already decided what happens to the edits, so the handler below lets that
    // close through instead of asking a second time.
    private bool _closeSettled;

    private async void OnApplyAndClose(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not CockpitViewModel cockpit)
        {
            _closeSettled = true;
            Close();
            return;
        }

        await cockpit.ApplyOptionsCommand.ExecuteAsync(null);

        // A profile the plugin config view rejected blocks the whole Apply (AC-1001 criterion 5) — stay open with
        // the error visible (Profiles.StatusMessage) rather than close over it.
        if (cockpit.OptionsApplyBlocked)
        {
            return;
        }

        _closeSettled = true;
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    // Every way out that is not Apply is a Cancel (AC-999) — the ✕ and Escape included, which is why this hangs
    // off Closing rather than off the Cancel button. Cancelling the close first is what lets the confirmation be
    // awaited: Avalonia will not hold a window open across an await on its own.
    private async void OnClosingDialog(object? sender, WindowClosingEventArgs e)
    {
        if (_closeSettled || DataContext is not CockpitViewModel cockpit)
        {
            return;
        }

        e.Cancel = true;

        if (cockpit.RefreshPendingOptionChanges())
        {
            var confirmation = new ConfirmationDialog
            {
                DataContext = new ConfirmationDialogViewModel(
                    "Discard your changes",
                    "Nothing you changed here has been saved yet. Closing puts every setting back the way it was "
                    + "when you opened this window.\n\n"
                    + "Turning encryption on or off, changing your password, checking for updates, running a backup "
                    + "and testing the microphone are not settings and already took effect — those stay.",
                    "Discard"),
            };

            if (!await confirmation.ShowDialog<bool>(this))
            {
                return;
            }
        }

        await cockpit.CancelOptionsCommand.ExecuteAsync(null);
        _closeSettled = true;
        Close();
    }

    private void OnRefreshDiagnostics(object? sender, RoutedEventArgs e) =>
        (DataContext as CockpitViewModel)?.Diagnostics.Refresh();

    // Deep-link (AC-1001): jumps the sidebar to the nav item whose Tag matches, e.g. "profiles". A tag nothing
    // matches (typo, a category renamed later) leaves the dialog on whatever it already had selected.
    public void SelectCategory(string tag)
    {
        if (CategoryNav.Items.OfType<ListBoxItem>().FirstOrDefault(item => item.Tag as string == tag) is { } match)
        {
            CategoryNav.SelectedItem = match;
        }
    }

    // Browse buttons for the Profiles category (AC-1001) — same file/folder pickers ManageProfilesDialog uses,
    // now against `cockpit.Profiles.SelectedProfile` instead of a dedicated dialog's own DataContext.
    private async void OnBrowseProfileConfigDir(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not CockpitViewModel { Profiles.SelectedProfile: { } profile })
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

    private async void OnBrowseProfileWorkingDirectory(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not CockpitViewModel { Profiles.SelectedProfile: { } profile })
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

    private async void OnBrowseProfileExecutable(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not CockpitViewModel { Profiles.SelectedProfile: { } profile })
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

    // Copying is a view's job (the clipboard is the window's), the same split as the file pickers below: the view
    // model builds the text, this hands it to the OS and lets the panel say it was copied.
    private async void OnCopyDiagnostics(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not CockpitViewModel cockpit || Clipboard is not { } clipboard)
        {
            return;
        }

        await clipboard.SetTextAsync(cockpit.Diagnostics.Report);
        cockpit.Diagnostics.MarkCopied();
    }

    // Turning encryption on or off rewrites every credential the operator has, and turning it off puts them all
    // back in the clear. Neither happens on a single click, and both say what they are about to do.
    private async void OnEnableEncryption(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not CockpitViewModel cockpit)
        {
            return;
        }

        var dialog = new PasswordDialog
        {
            DataContext = new PasswordDialogViewModel(
                "Encrypt your credentials",
                "Your API keys and tokens are encrypted in cockpit.json, and the cockpit asks for this password "
                + "every time it starts.\n\n"
                + "If you forget it, nobody can decrypt them — not you, not us. The only way back is to clear the "
                + "credentials and type them in again; your profiles, sessions, layout and shortcuts survive that. "
                + "You can turn encryption off again at any time, which puts everything back exactly as it was.",
                requiresCurrent: false),
        };

        if (await dialog.ShowDialog<PasswordDialogViewModel?>(this) is { } password)
        {
            await cockpit.Security.EnableAsync(password.NewPassword);
        }
    }

    private async void OnDisableEncryption(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not CockpitViewModel cockpit)
        {
            return;
        }

        var confirmation = new ConfirmationDialog
        {
            DataContext = new ConfirmationDialogViewModel(
                "Turn off encryption",
                "Your API keys and tokens go back to being readable in cockpit.json, and the cockpit will start "
                + "without asking for a password. Nothing is lost — this is the exact reverse of turning it on.",
                "Turn it off"),
        };

        if (await confirmation.ShowDialog<bool>(this))
        {
            await cockpit.Security.DisableAsync();
        }
    }

    private async void OnChangePassword(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not CockpitViewModel cockpit)
        {
            return;
        }

        var dialog = new PasswordDialog
        {
            DataContext = new PasswordDialogViewModel(
                "Change your password",
                "Your credentials are decrypted with the old password and encrypted again with the new one.",
                requiresCurrent: true),
        };

        if (await dialog.ShowDialog<PasswordDialogViewModel?>(this) is { } password)
        {
            await cockpit.Security.ChangePasswordAsync(password.CurrentPassword, password.NewPassword);
        }
    }

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
            // The one string here that names the app: this dialog is the OS's, drawn among windows from every
            // other program, where "back up the cockpit" says nothing about whose backup it is.
            Title = $"Back up {CockpitProduct.DisplayName}",
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

    // The assistant's own memory, exported/restored on its own (AC-657) — same file-picker split as the backup
    // above, but no selection dialog on the way in: there is nothing to choose, only these two files.
    private async void OnExportAssistantMemory(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not CockpitViewModel cockpit)
        {
            return;
        }

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export the assistant's memory",
            SuggestedFileName = $"assistant-memory-{DateTime.Now:yyyy-MM-dd}.zip",
            DefaultExtension = "zip",
            FileTypeChoices = [new FilePickerFileType("Assistant memory") { Patterns = ["*.zip"] }],
        });

        if (file?.TryGetLocalPath() is { Length: > 0 } path)
        {
            await cockpit.ExportAssistantMemoryAsync(path);
        }
    }

    private async void OnImportAssistantMemory(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not CockpitViewModel cockpit)
        {
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import the assistant's memory",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Assistant memory") { Patterns = ["*.zip"] }],
        });

        if (files.FirstOrDefault()?.TryGetLocalPath() is { Length: > 0 } path)
        {
            await cockpit.ImportAssistantMemoryAsync(path);
        }
    }
}
