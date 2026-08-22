using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.GitStatus;

// The settings view (opened from the plugin manager's gear): the session-header indicator's one setting —
// whether it shows the branch name next to its status dot (AC-36). Implements `IPluginSettingsView`
// so the host dialog shows a Save button; the host performs the write it hands back (AC-1003).
//
// AC-522 removed the repository-list section this view used to show above the toggle below (managing the
// repos the plugin's now-removed dialog watched) — see `GitStatusSettings` for what that leaves
// behind in storage.
internal sealed class GitStatusSettingsControl : UserControl, IPluginSettingsView
{
    private readonly GitStatusSettings _settings;
    private readonly CheckBox _showBranchName;

    public GitStatusSettingsControl(ICockpitHost host, GitStatusSettings settings)
    {
        _settings = settings;

        _showBranchName = new CheckBox
        {
            Content = "Show the branch name in the session header (off = dot only, name on hover)",
            IsChecked = settings.ShowBranchName,
        };

        // AC-1033: the `?` the SDK draws, pointing at the section of this plugin's own page that explains
        // when the branch name is worth showing. Handed over unconditionally — it hides itself if this plugin's
        // documentation is ever not there, so there is no second condition to keep in step with the files.
        var toggleRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { _showBranchName, host.CreateHelpHint("git-status", "branch-name") },
        };

        Content = new StackPanel
        {
            Margin = new Thickness(4),
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = "Header badge", FontWeight = FontWeight.SemiBold },
                toggleRow,
            },
        };
    }

    // AC-1004, criterion 3: the old `Save()` was this one property write. `GitStatusSettings.ShowBranchName`
    // raises `Changed` from its own setter, so the header badge's refresh rides with the write into the commit.
    public bool TryStage(out Action? commit, out string? error)
    {
        commit = () => _settings.ShowBranchName = _showBranchName.IsChecked ?? true;
        error = null;
        return true;
    }
}
