namespace Cockpit.Core.Shortcuts;

// The user's configured gestures (#: shortcuts): per-action for the built-in app actions (defaulting to
// `ShortcutCatalog` where unset), and per-id overrides for plugin-contributed shortcuts (which
// otherwise use the gesture the plugin registered). A blank gesture unbinds. Immutable; the `With…`
// helpers return a new instance and the store persists it.
public sealed record ShortcutSettings(
    IReadOnlyDictionary<ShortcutAction, string> Gestures,
    IReadOnlyDictionary<string, string> PluginGestures)
{
    // Every app action bound to its catalog default, and no plugin overrides.
    public static ShortcutSettings Default { get; } =
        new(ShortcutCatalog.All.ToDictionary(descriptor => descriptor.Action, descriptor => descriptor.DefaultGesture),
            new Dictionary<string, string>());

    // The gesture bound to `action`, falling back to the catalog default when unset.
    public string GestureFor(ShortcutAction action) =>
        Gestures.TryGetValue(action, out var gesture) ? gesture : ShortcutCatalog.DefaultGesture(action);

    // The user's override gesture for a plugin shortcut `shortcutId`, or `pluginDefault` when the user never changed it.
    public string GestureForPlugin(string shortcutId, string pluginDefault) =>
        PluginGestures.TryGetValue(shortcutId, out var gesture) ? gesture : pluginDefault;

    // Returns a copy with `action` bound to `gesture` (trimmed; null/blank unbinds it).
    public ShortcutSettings With(ShortcutAction action, string? gesture)
    {
        var map = new Dictionary<ShortcutAction, string>(Gestures)
        {
            [action] = gesture?.Trim() ?? string.Empty,
        };
        return this with { Gestures = map };
    }

    // Returns a copy with the plugin shortcut `shortcutId` overridden to `gesture` (trimmed; null/blank unbinds it).
    public ShortcutSettings WithPlugin(string shortcutId, string? gesture)
    {
        var map = new Dictionary<string, string>(PluginGestures)
        {
            [shortcutId] = gesture?.Trim() ?? string.Empty,
        };
        return this with { PluginGestures = map };
    }
}
