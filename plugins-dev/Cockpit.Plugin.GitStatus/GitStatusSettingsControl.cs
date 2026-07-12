using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
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
        var addButton = new Button { Content = "Add" };
        addButton.Click += (_, _) => _Add();

        var addRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        addRow.Children.Add(_newRepo);
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
                    Content = "✕",
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

    public bool Save()
    {
        _settings.SaveRepos([.. _repos]);
        return true;
    }

    private static IBrush? _Brush(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is IBrush brush ? brush : null;
}
