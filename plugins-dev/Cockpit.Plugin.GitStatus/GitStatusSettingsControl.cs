using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.GitStatus;

/// <summary>
/// The settings view (opened from the plugin manager's gear): the session-header indicator's one setting —
/// whether it shows the branch name next to its status dot (AC-36). Implements <see cref="IPluginSettingsView"/>
/// so the host dialog shows a Save button.
/// <para>
/// AC-522 removed the repository-list section this view used to show above the toggle below (managing the
/// repos the plugin's now-removed dialog watched) — see <see cref="GitStatusSettings"/> for what that leaves
/// behind in storage.
/// </para>
/// </summary>
internal sealed class GitStatusSettingsControl : UserControl, IPluginSettingsView
{
    private readonly GitStatusSettings _settings;
    private readonly CheckBox _showBranchName;

    public GitStatusSettingsControl(GitStatusSettings settings)
    {
        _settings = settings;

        _showBranchName = new CheckBox
        {
            Content = "Show the branch name in the session header (off = dot only, name on hover)",
            IsChecked = settings.ShowBranchName,
        };

        Content = new StackPanel
        {
            Margin = new Thickness(4),
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = "Header badge", FontWeight = FontWeight.SemiBold },
                _showBranchName,
            },
        };
    }

    public bool Save()
    {
        _settings.ShowBranchName = _showBranchName.IsChecked ?? true;
        return true;
    }
}
