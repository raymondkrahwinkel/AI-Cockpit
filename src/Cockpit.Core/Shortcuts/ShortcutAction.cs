namespace Cockpit.Core.Shortcuts;

/// <summary>
/// The app-level actions a keyboard shortcut can trigger (#: shortcuts). These map one-to-one to the main
/// menu's commands; the plugin-contributed shortcuts are a separate, open-ended set keyed by string id.
/// </summary>
public enum ShortcutAction
{
    NewSession,
    ManageProfiles,
    McpServers,
    PluginStore,
    Options,
    About,
    ToggleZoom,
    SearchTranscripts,
    CommandPalette,
    PreviousSession,
    NextSession,
}
