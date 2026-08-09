namespace Cockpit.App.ViewModels;

// Which of the five CockpitCategoryTint*Brush resources (Theme.axaml, AC-553) a plugin's logo tile takes —
// picked by a stable hash of the category string so the same category always lands on the same tint, a new
// category never needs a hand-added mapping, and the store never has to carry a colour per plugin.
internal static class PluginCategoryTint
{
    private static readonly string[] BrushKeys =
    [
        "CockpitCategoryTintBlueBrush",
        "CockpitCategoryTintCyanBrush",
        "CockpitCategoryTintAmberBrush",
        "CockpitCategoryTintGreenBrush",
        "CockpitCategoryTintPurpleBrush",
    ];

    public static string BrushKeyFor(string category)
    {
        if (string.IsNullOrWhiteSpace(category))
        {
            return BrushKeys[0];
        }

        return BrushKeys[_StableHash(category) % BrushKeys.Length];
    }

    // string.GetHashCode() is randomised per process (hash-flooding protection) — fine for a dictionary, wrong
    // here, where the same category has to land on the same tint on every run and on every machine.
    private static int _StableHash(string value)
    {
        unchecked
        {
            var hash = 23;
            foreach (var character in value)
            {
                hash = hash * 31 + character;
            }

            return hash & int.MaxValue;
        }
    }
}
