using Cockpit.Core.Shortcuts;

namespace Cockpit.Infrastructure.Configuration;

// On-disk shape of the app-action shortcuts, under the `shortcuts` section of `cockpit.json`: a map
// from `ShortcutAction` name to gesture string. Kept separate from `ShortcutSettings`
// so the persisted shape can evolve independently; unknown action names are ignored on load.
internal sealed class ShortcutSettingsEntry
{
    public Dictionary<string, string> Gestures { get; set; } = [];

    // Per-plugin-shortcut gesture overrides, keyed by the plugin's shortcut id.
    public Dictionary<string, string> PluginGestures { get; set; } = [];

    public static ShortcutSettingsEntry FromDomain(ShortcutSettings settings) => new()
    {
        Gestures = settings.Gestures.ToDictionary(pair => pair.Key.ToString(), pair => pair.Value),
        PluginGestures = new Dictionary<string, string>(settings.PluginGestures),
    };

    public ShortcutSettings ToDomain()
    {
        var map = new Dictionary<ShortcutAction, string>();
        foreach (var (name, gesture) in Gestures)
        {
            if (Enum.TryParse<ShortcutAction>(name, out var action))
            {
                map[action] = gesture;
            }
        }

        // Fill any actions the file did not carry (older file, new action) from the defaults.
        foreach (var descriptor in ShortcutCatalog.All)
        {
            map.TryAdd(descriptor.Action, descriptor.DefaultGesture);
        }

        return new ShortcutSettings(map, new Dictionary<string, string>(PluginGestures));
    }
}
