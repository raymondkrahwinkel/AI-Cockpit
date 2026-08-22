using Avalonia.Controls;
using Avalonia.Media;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Diagram;

// The plugin's settings view (all code-behind Avalonia, like the other plugins). In the plugin root, not a Ui/
// submap — this plugin already keeps DiagramWindow/DiagramWorkspaceBody/etc. there, so its settings view follows.
internal sealed class DiagramSettingsControl : UserControl, IPluginSettingsView
{
    private readonly DiagramSettings _settings;
    private readonly CheckBox _skipDiagram;
    private readonly CheckBox _skipWhiteboard;
    private readonly CheckBox _skipWireframe;

    public DiagramSettingsControl(ICockpitHost host, DiagramSettings settings)
    {
        _settings = settings;

        _skipDiagram = new CheckBox { Content = "Skip Diagram consent", IsChecked = settings.SkipDiagramConsent };
        _skipWhiteboard = new CheckBox { Content = "Skip Whiteboard consent", IsChecked = settings.SkipWhiteboardConsent };
        _skipWireframe = new CheckBox { Content = "Skip Wireframe consent", IsChecked = settings.SkipWireframeConsent };

        Content = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                // AC-1043: the SDK-drawn "?" beside the consent checkboxes, pointing at this plugin's own
                // Docs page section that explains them in full.
                new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    Children = { new TextBlock { Text = "Agent consent" }, host.CreateHelpHint("collaboration", "consent-toggle") },
                },
                _skipDiagram,
                _skipWhiteboard,
                _skipWireframe,
                new TextBlock
                {
                    Text = "Each box lets that surface's open/read/edit tools go straight through, with no "
                        + "Approve/Deny prompt and no line in the consent history. Off is the default and asks "
                        + "every time; the \"Laat sdk meekijken\" button on the whiteboard is unaffected either way.",
                    Opacity = 0.7,
                    TextWrapping = TextWrapping.Wrap,
                },
            },
        };
    }

    // AC-1004, criterion 3: the old `Save()` was these three consent toggles and nothing else — no side effect to
    // place. Every reader takes the setting fresh off storage on the next prompt, so nothing needs telling.
    public bool TryStage(out Action? commit, out string? error)
    {
        commit = _Commit;
        error = null;
        return true;
    }

    private void _Commit()
    {
        _settings.SkipDiagramConsent = _skipDiagram.IsChecked ?? false;
        _settings.SkipWhiteboardConsent = _skipWhiteboard.IsChecked ?? false;
        _settings.SkipWireframeConsent = _skipWireframe.IsChecked ?? false;
    }
}
