using Cockpit.Core.Abstractions.Hotkeys;

namespace Cockpit.Core.Hotkeys;

/// <summary>
/// Finds two armed hotkeys asking for the same key. Both backends have to pick one when that happens — the
/// keyboard hook keys its map by key code, the portal binds per shortcut id and leaves the compositor to it —
/// so without this the operator sets a key, one of the two features stops working, and nothing says why.
/// </summary>
public static class GlobalHotkeyConflictCheck
{
    /// <summary>
    /// A sentence naming the clash to show in the settings, or <see langword="null"/> when every armed key is
    /// its own. Only the first clash is reported: there are two hotkeys, and a list of one reads worse than a
    /// sentence.
    /// </summary>
    public static string? Describe(IReadOnlyList<GlobalHotkeyBinding> bindings)
    {
        for (var i = 0; i < bindings.Count; i++)
        {
            for (var j = i + 1; j < bindings.Count; j++)
            {
                if (string.Equals(bindings[i].KeyName, bindings[j].KeyName, StringComparison.OrdinalIgnoreCase))
                {
                    return $"“{bindings[i].Description}” and “{bindings[j].Description}” are both set to {bindings[i].KeyName}. Only one of them will fire.";
                }
            }
        }

        return null;
    }
}
