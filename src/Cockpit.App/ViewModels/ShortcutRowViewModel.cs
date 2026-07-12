using CommunityToolkit.Mvvm.ComponentModel;
using Cockpit.Core.Shortcuts;

namespace Cockpit.App.ViewModels;

/// <summary>
/// One row in the Options → Shortcuts tab: an action's <see cref="Label"/> and its editable
/// <see cref="Gesture"/> (recorded by clicking and pressing keys). An app-action row carries its
/// <see cref="Action"/>; a plugin-contributed row carries its <see cref="PluginShortcutId"/>. Both are
/// editable — the app-action gesture and the plugin override are persisted separately by
/// <see cref="CockpitViewModel"/>.
/// </summary>
public partial class ShortcutRowViewModel : ViewModelBase
{
    public string Label { get; }

    /// <summary>Non-null for a built-in app action; null for a plugin-contributed shortcut.</summary>
    public ShortcutAction? Action { get; }

    /// <summary>Non-null for a plugin-contributed shortcut (its stable id); null for an app action.</summary>
    public string? PluginShortcutId { get; }

    [ObservableProperty]
    private string _gesture;

    /// <summary>App-action row.</summary>
    public ShortcutRowViewModel(string label, ShortcutAction action, string gesture)
    {
        Label = label;
        Action = action;
        _gesture = gesture;
    }

    /// <summary>Plugin-contributed shortcut row (editable override, keyed by its id).</summary>
    public ShortcutRowViewModel(string label, string pluginShortcutId, string gesture)
    {
        Label = label;
        PluginShortcutId = pluginShortcutId;
        _gesture = gesture;
    }
}
