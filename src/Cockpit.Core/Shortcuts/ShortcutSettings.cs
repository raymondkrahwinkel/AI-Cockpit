namespace Cockpit.Core.Shortcuts;

/// <summary>
/// The user's configured gestures (#: shortcuts): per-action for the built-in app actions (defaulting to
/// <see cref="ShortcutCatalog"/> where unset), and per-id overrides for plugin-contributed shortcuts (which
/// otherwise use the gesture the plugin registered). A blank gesture unbinds. Immutable; the <c>With…</c>
/// helpers return a new instance and the store persists it.
/// </summary>
public sealed record ShortcutSettings(
    IReadOnlyDictionary<ShortcutAction, string> Gestures,
    IReadOnlyDictionary<string, string> PluginGestures)
{
    /// <summary>Every app action bound to its catalog default, and no plugin overrides.</summary>
    public static ShortcutSettings Default { get; } =
        new(ShortcutCatalog.All.ToDictionary(descriptor => descriptor.Action, descriptor => descriptor.DefaultGesture),
            new Dictionary<string, string>());

    /// <summary>The gesture bound to <paramref name="action"/>, falling back to the catalog default when unset.</summary>
    public string GestureFor(ShortcutAction action) =>
        Gestures.TryGetValue(action, out var gesture) ? gesture : ShortcutCatalog.DefaultGesture(action);

    /// <summary>The user's override gesture for a plugin shortcut <paramref name="shortcutId"/>, or <paramref name="pluginDefault"/> when the user never changed it.</summary>
    public string GestureForPlugin(string shortcutId, string pluginDefault) =>
        PluginGestures.TryGetValue(shortcutId, out var gesture) ? gesture : pluginDefault;

    /// <summary>Returns a copy with <paramref name="action"/> bound to <paramref name="gesture"/> (trimmed; null/blank unbinds it).</summary>
    public ShortcutSettings With(ShortcutAction action, string? gesture)
    {
        var map = new Dictionary<ShortcutAction, string>(Gestures)
        {
            [action] = gesture?.Trim() ?? string.Empty,
        };
        return this with { Gestures = map };
    }

    /// <summary>Returns a copy with the plugin shortcut <paramref name="shortcutId"/> overridden to <paramref name="gesture"/> (trimmed; null/blank unbinds it).</summary>
    public ShortcutSettings WithPlugin(string shortcutId, string? gesture)
    {
        var map = new Dictionary<string, string>(PluginGestures)
        {
            [shortcutId] = gesture?.Trim() ?? string.Empty,
        };
        return this with { PluginGestures = map };
    }
}
