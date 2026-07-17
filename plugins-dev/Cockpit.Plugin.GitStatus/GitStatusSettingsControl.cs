using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Material.Icons;
using Material.Icons.Avalonia;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.GitStatus;

/// <summary>
/// The settings view (opened from the plugin manager's gear): manage the list of repository paths whose
/// status the dialog shows (#1). Add a path, remove one, then Save. Implements
/// <see cref="IPluginSettingsView"/> so the host dialog shows a Save button.
/// </summary>
internal sealed class GitStatusSettingsControl : UserControl, IPluginSettingsView
{
    private readonly GitStatusSettings _settings;
    private readonly ObservableCollection<string> _repos;
    private readonly TextBox _newRepo;

    public GitStatusSettingsControl(GitStatusSettings settings)
    {
        _settings = settings;
        _repos = [.. settings.Repos];

        _newRepo = new TextBox { PlaceholderText = @"Repository path, e.g. D:\Projects\myrepo", Width = 360 };

        // A folder picker beside the box (#AC-15): typing a path is fine when you know it, but the repo you want
        // to add is usually one you can point at faster than you can spell. Browse fills the box and adds it in
        // one go — "select the folder you want to add" — while the box stays there for the paths you do know.
        var browseButton = new Button { Content = "Browse…" };
        browseButton.Click += async (_, _) => await _BrowseAsync();

        var addButton = new Button { Content = "Add" };
        addButton.Click += (_, _) => _Add();

        var addRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        addRow.Children.Add(_newRepo);
        addRow.Children.Add(browseButton);
        addRow.Children.Add(addButton);

        var list = new ItemsControl
        {
            ItemsSource = _repos,
            ItemTemplate = new FuncDataTemplate<string>((path, _) =>
            {
                var text = new TextBlock
                {
                    Text = path,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };
                var remove = new Button
                {
                    Content = new MaterialIcon { Kind = MaterialIconKind.Close, Width = 12, Height = 12 },
                    Classes = { "Compact", "Subtle" },
                    Foreground = _Brush("CockpitTextFaintBrush"),
                };
                remove.Click += (_, _) => _repos.Remove(path);

                var row = new DockPanel { Margin = new Thickness(0, 2, 0, 0) };
                DockPanel.SetDock(remove, Dock.Right);
                row.Children.Add(remove);
                row.Children.Add(text);
                return row;
            }, true),
        };

        Content = new StackPanel
        {
            Margin = new Thickness(4),
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = "Repositories", FontWeight = FontWeight.SemiBold },
                new TextBlock
                {
                    Text = "The git repositories to show status for. Add the full path to each repo's working directory.",
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = _Brush("CockpitTextFaintBrush"),
                },
                addRow,
                list,
            },
        };
    }

    private void _Add()
    {
        var path = _newRepo.Text?.Trim();
        if (!string.IsNullOrEmpty(path) && !_repos.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            _repos.Add(path);
            _newRepo.Text = string.Empty;
        }
    }

    // The folder picker behind Browse. This control is a UserControl, not a Window, so the picker is reached
    // through the top-level's StorageProvider rather than a window of our own — the same route the app's own
    // working-directory picker takes. A picked folder fills the box and is added straight away; cancelling
    // leaves whatever was already typed untouched.
    private async Task _BrowseAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return;
        }

        // Start where the half-typed path points, when it points somewhere: browsing from the folder you were
        // already heading to beats starting at the drive root every time.
        var current = _newRepo.Text?.Trim();
        var start = string.IsNullOrEmpty(current) ? null : await storage.TryGetFolderFromPathAsync(current);

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose a repository folder",
            AllowMultiple = false,
            SuggestedStartLocation = start,
        });

        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
        {
            _newRepo.Text = path;
            _Add();
        }
    }

    public bool Save()
    {
        _settings.SaveRepos([.. _repos]);
        return true;
    }

    private static IBrush? _Brush(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is IBrush brush ? brush : null;
}
