using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.Plugin.GitStatus.UI;

// The settings view behind the plugin manager's gear: whether the header badge shows the branch name (AC-36).
// An IPluginSettingsView, so the host dialog shows Save and performs the write it hands back (AC-1003); the
// repository list AC-522 removed used to sit above this toggle.
internal sealed class GitStatusSettingsControl : UserControl, IPluginSettingsView
{
    private readonly GitStatusSettings _settings;
    private readonly CheckBox _showBranchName;

    public GitStatusSettingsControl(ICockpitUiHost host, GitStatusSettings settings)
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
