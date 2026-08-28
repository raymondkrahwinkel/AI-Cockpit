using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Cockpit.App.Controls;
using Cockpit.App.Converters;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Backup;
using Cockpit.Core.Configuration;
using Cockpit.Core.Help;
using Microsoft.Extensions.DependencyInjection;
using Material.Icons;
using Material.Icons.Avalonia;

namespace Cockpit.App.Views;

// #13: categorised replacement for the sidebar's Options flyout, which had grown too tall for a
// popup. DataContext is the shared CockpitViewModel. The PLUGINS group (AC-1005) is built here in
// code, not XAML, since its rows come from whichever plugins are installed this session.
public partial class OptionsDialog : Window
{
    private readonly Dictionary<string, PluginOptionsRowViewModel> _pluginRows = new(StringComparer.Ordinal);
    // AC-1040: a `?` beside each page's title, landing on the section of the knowledge base that describes that
    // page — not on the top of a long article. Two of them leave this dialog on purpose: the assistant is a
    // feature with a page of its own, and where isolated sessions put their work is the worktrees page's subject.
    private static readonly (string Slot, HelpAddress Target)[] Help =
    [
        ("SessionsHelp", new HelpAddress("settings", "sessions")),
        ("WhereWorkLandsHelp", new HelpAddress("worktrees", "where-they-live")),
        ("ProfilesHelp", new HelpAddress("settings", "profiles")),
        ("AppearanceHelp", new HelpAddress("settings", "appearance")),
        ("TerminalHelp", new HelpAddress("settings", "terminal")),
        ("NotificationsHelp", new HelpAddress("settings", "notifications")),
        ("ShortcutsHelp", new HelpAddress("settings", "shortcuts")),
        ("VoiceHelp", new HelpAddress("settings", "voice")),
        ("AssistantHelp", new HelpAddress("assistant", "turning-it-on")),
        ("SecurityHelp", new HelpAddress("settings", "security")),
        ("McpServersHelp", new HelpAddress("settings", "mcp-servers")),
        ("NodesHelp", new HelpAddress("settings", "nodes")),
        ("BackupHelp", new HelpAddress("settings", "backup")),
        ("UpdatesHelp", new HelpAddress("settings", "updates")),
        ("DebugHelp", new HelpAddress("settings", "debug")),
    ];

    public OptionsDialog()
    {
        InitializeComponent();
        CockpitWindowChrome.Apply(this);
        _AddHelpHints();
        CategoryNav.SelectionChanged += (_, _) => _EnsurePluginContent();

        // The plugin list is built when the dialog opens rather than when the app started: a plugin installed since
        // then should not be missing from its own backup. The diagnostics panel is read the same way — once, on
        // open — so the Debug tab shows current figures without a timer running behind a page nobody is looking at.
        Opened += (_, _) =>
        {
            if (DataContext is CockpitViewModel cockpit)
            {
                cockpit.RefreshBackupPlugins();
                cockpit.Diagnostics.Refresh();
                _BuildPluginCategories(cockpit);
                RequestAnimationFrame(_ => RequestAnimationFrame(_ => cockpit.OptionsOpenPresented()));
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

    // Each hint hides itself when its page is not there, so a build without the documentation reads exactly as
    // this dialog did before.
    private void _AddHelpHints()
    {
        if (Program.Services?.GetService<HelpService>() is not { } help)
        {
            return;
        }

        foreach (var (slot, target) in Help)
        {
            this.FindControl<StackPanel>(slot)?.Children.Add(new HelpHint(help, target, origin: "a “?” in Options"));
        }
    }

    private async void OnApplyAndClose(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not CockpitViewModel cockpit)
        {
            _closeSettled = true;
            Close();
            return;
        }

        await cockpit.ApplyOptionsCommand.ExecuteAsync(null);

        // AC-1005/AC-1001 criterion 5: a rejected profile or plugin settings row blocks the whole
        // Apply — stay open with the error visible. PluginSettingsError names which row refused,
        // since the operator may be looking at a different category.
        PluginSettingsErrorText.IsVisible = cockpit.PluginSettingsError is { Length: > 0 };
        PluginSettingsErrorText.Text = cockpit.PluginSettingsError;

        if (cockpit.OptionsApplyBlocked)
        {
            return;
        }

        _closeSettled = true;
        Close();
    }

    // Criterion 7: search keywords for a plugin's row ("docker" must also find Docker inside Local
    // CI). No way to derive these from view content, so hand-authored per known plugin; one with
    // no entry still gets a row and searches on its name alone.
    private static readonly Dictionary<string, string> _PluginSearchKeywords = new(StringComparer.Ordinal)
    {
        ["youtrack"] = "youtrack issue tracker",
        ["docker"] = "docker containers images engine",
        ["local-ci"] = "local ci docker run tests workflow jobs",
        ["autopilot"] = "autopilot",
        ["depot"] = "depot storage artifacts",
        ["diagram"] = "diagram",
        ["github-issues"] = "github issues",
        ["github-pull-requests"] = "github pull requests",
        ["git-status"] = "git status",
        ["kubernetes"] = "kubernetes k8s",
        ["system-monitor"] = "system monitor cpu memory",
        ["workflows"] = "workflows",
    };

    // AC-1030: declared plugin-settings groups, in the fixed order they render below the static WORKING/VOICE &
    // ASSISTANT/SYSTEM groups from the .axaml. A plugin that declares none of these (Category is null) lands in
    // the trailing "PLUGINS" catch-all, exactly where every plugin landed before this existed.
    private static readonly string[] _PluginCategoryOrder = ["Assistant Plugins"];

    private const string _DefaultPluginCategory = "PLUGINS";

    // Builds one nav group per declared plugin-settings category, appended into the same CategoryNav/
    // CategoryContent the static categories use, so the sidebar stays one scroll region and selection scope.
    // Skipped entirely when no plugin registered a settings view.
    private void _BuildPluginCategories(CockpitViewModel cockpit)
    {
        if (cockpit.PluginOptionsRows.Count == 0)
        {
            return;
        }

        var byCategory = cockpit.PluginOptionsRows.ToLookup(row => row.Category ?? _DefaultPluginCategory);

        foreach (var category in _PluginCategoryOrder.Append(_DefaultPluginCategory))
        {
            if (!byCategory[category].Any())
            {
                continue;
            }

            _BuildPluginCategory(category, byCategory[category]);
        }

        // Criterion 8: a small, non-clickable note pointing at the Plugin Store instead of any discovery or
        // install affordance living in Options itself.
        CategoryNav.Items.Add(new ListBoxItem
        {
            Focusable = false,
            IsHitTestVisible = false,
            Margin = new Thickness(10, 4, 10, 0),
            Content = new TextBlock
            {
                Text = "Finding and installing new plugins happens in the separate Plugin store window.",
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = _Brush("CockpitTextFaintBrush"),
            },
        });
    }

    private void _BuildPluginCategory(string category, IEnumerable<PluginOptionsRowViewModel> rows)
    {
        CategoryNav.Items.Add(new ListBoxItem
        {
            Classes = { "navGroupHeader" },
            Focusable = false,
            IsHitTestVisible = false,
            Content = new TextBlock { Classes = { "subnavGroup" }, Text = category.ToUpperInvariant() },
        });

        foreach (var row in rows)
        {
            var tag = $"plugin:{row.PluginId}";
            var keywords = _PluginSearchKeywords.GetValueOrDefault(row.PluginId, row.DisplayName);

            CategoryNav.Items.Add(_BuildPluginNavItem(tag, row.DisplayName, keywords));
            _pluginRows.Add(tag, row);
        }
    }

    private static readonly IValueConverter _HasSearchTextConverter =
        new FuncValueConverter<string?, bool>(text => !string.IsNullOrEmpty(text));

    private static ListBoxItem _BuildPluginNavItem(string tag, string displayName, string keywords)
    {
        var icon = new MaterialIcon
        {
            Kind = MaterialIconKind.Puzzle,
            Width = 15,
            Height = 15,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        Grid.SetColumn(icon, 0);

        var label = new TextBlock { Text = displayName, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(label, 1);

        var badgeText = new TextBlock();
        badgeText.Bind(TextBlock.TextProperty, new Binding(nameof(CockpitViewModel.OptionsSearchText))
        {
            Converter = OptionsCategoryMatchCountConverter.Instance,
            ConverterParameter = keywords,
        });
        var badge = new Border { Classes = { "searchMatchBadge" }, Child = badgeText };
        badge.Bind(IsVisibleProperty, new Binding(nameof(CockpitViewModel.OptionsSearchText)) { Converter = _HasSearchTextConverter });
        Grid.SetColumn(badge, 2);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        grid.Children.Add(icon);
        grid.Children.Add(label);
        grid.Children.Add(badge);

        var item = new ListBoxItem { Tag = tag, Content = grid };
        item.Bind(IsVisibleProperty, new Binding(nameof(CockpitViewModel.OptionsSearchText))
        {
            Converter = OptionsCategoryVisibleConverter.Instance,
            ConverterParameter = keywords,
        });
        return item;
    }

    private ScrollViewer _BuildPluginContent(string tag, PluginOptionsRowViewModel row)
    {
        row.EnsureContent();
        var body = new StackPanel { Margin = new Thickness(24, 20), MaxWidth = 900, Spacing = 8 };

        // AC-1033's second door, and the one the question usually comes through: where you are pasting a token
        // is where you wonder which token it wants. Beside the plugin's name, and only when that plugin ships
        // documentation — a link to an empty page is worse than no link.
        var heading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        heading.Children.Add(new TextBlock { Text = row.DisplayName, FontSize = 20, FontWeight = FontWeight.Bold });
        if (Program.Services?.GetService<HelpService>() is { } help && help.LandingFor(row.PluginId) is { } landing)
        {
            heading.Children.Add(new HelpHint(
                help, landing, "Documentation", $"the Documentation link on {row.DisplayName}'s settings"));
        }

        body.Children.Add(heading);
        body.Children.Add(new Border { Height = 1, Background = _Brush("CockpitHairlineBrush"), Margin = new Thickness(0, 4) });

        // No footer of its own and no window of its own (criteria 3/5): the view sits flat in the content column,
        // under the same shared Apply and Close the rest of Options uses.
        body.Children.Add(row.Content is { } content
            ? content
            : new TextBlock
            {
                Text = row.UnavailableReason,
                TextWrapping = TextWrapping.Wrap,
                Foreground = _Brush("CockpitTextSecondaryBrush"),
            });

        var scroll = new ScrollViewer { Tag = tag, Content = body };
        // ElementName bindings need a NameScope a code-behind element never gets (AC-1011), unlike the nav
        // item's DataContext-relative binding above — bind straight to the already-resolved CategoryNav
        // instance instead.
        scroll.Bind(IsVisibleProperty, new Binding
        {
            Source = CategoryNav,
            Path = nameof(ListBox.SelectedItem),
            Converter = CategoryTagEqualsConverter.Instance,
            ConverterParameter = tag,
        });
        return scroll;
    }

    private void _EnsurePluginContent()
    {
        if (CategoryNav.SelectedItem is not ListBoxItem { Tag: string tag }
            || !_pluginRows.TryGetValue(tag, out var row)
            || CategoryContent.Children.OfType<Control>().Any(content => Equals(content.Tag, tag)))
        {
            return;
        }

        CategoryContent.Children.Add(_BuildPluginContent(tag, row));
    }

    private static IBrush? _Brush(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is IBrush brush ? brush : null;

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
            return;
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
