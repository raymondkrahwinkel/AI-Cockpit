using CommunityToolkit.Mvvm.ComponentModel;
using Cockpit.Core.Shortcuts;

namespace Cockpit.App.ViewModels;

// One row in the Options → Shortcuts tab: an action's `Label` and its editable
// `Gesture` (recorded by clicking and pressing keys). An app-action row carries its
// `Action`; a plugin-contributed row carries its `PluginShortcutId`. Both are
// editable — the app-action gesture and the plugin override are persisted separately by
// `CockpitViewModel`.
public partial class ShortcutRowViewModel : ViewModelBase
{
    public string Label { get; }

    // Non-null for a built-in app action; null for a plugin-contributed shortcut.
    public ShortcutAction? Action { get; }

    // Non-null for a plugin-contributed shortcut (its stable id); null for an app action.
    public string? PluginShortcutId { get; }

    [ObservableProperty]
    private string _gesture;

    // App-action row.
    public ShortcutRowViewModel(string label, ShortcutAction action, string gesture)
    {
        Label = label;
        Action = action;
        _gesture = gesture;
    }

    // Plugin-contributed shortcut row (editable override, keyed by its id).
    public ShortcutRowViewModel(string label, string pluginShortcutId, string gesture)
    {
        Label = label;
        PluginShortcutId = pluginShortcutId;
        _gesture = gesture;
    }
}
