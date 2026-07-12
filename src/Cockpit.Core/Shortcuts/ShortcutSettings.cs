namespace Cockpit.Core.Shortcuts;

/// <summary>
/// The user's configured gestures for the app actions (#: shortcuts) — a per-action gesture string, defaulting
/// to <see cref="ShortcutCatalog"/> where unset. A blank gesture means the action is unbound. Immutable; the
/// <see cref="With"/> helper returns a new instance and the store persists it.
/// </summary>
public sealed record ShortcutSettings(IReadOnlyDictionary<ShortcutAction, string> Gestures)
{
    /// <summary>Every action bound to its catalog default.</summary>
    public static ShortcutSettings Default { get; } =
        new(ShortcutCatalog.All.ToDictionary(descriptor => descriptor.Action, descriptor => descriptor.DefaultGesture));

    /// <summary>The gesture bound to <paramref name="action"/>, falling back to the catalog default when unset.</summary>
    public string GestureFor(ShortcutAction action) =>
        Gestures.TryGetValue(action, out var gesture) ? gesture : ShortcutCatalog.DefaultGesture(action);

    /// <summary>Returns a copy with <paramref name="action"/> bound to <paramref name="gesture"/> (trimmed; null/blank unbinds it).</summary>
    public ShortcutSettings With(ShortcutAction action, string? gesture)
    {
        var map = new Dictionary<ShortcutAction, string>(Gestures)
        {
            [action] = gesture?.Trim() ?? string.Empty,
        };
        return new ShortcutSettings(map);
    }
}
