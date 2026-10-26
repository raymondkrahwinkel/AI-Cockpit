namespace Cockpit.Core.Plugins;

// Compares two plugin version strings for update detection (#14). Numeric versions (`1.2.0`) compare
// by `System.Version`; anything that does not parse falls back to a plain inequality, so a
// non-numeric bump (`1.2.0-beta` → `1.2.0`) still surfaces as an available update.
public static class PluginVersion
{
    // AC-1500: a bundled plugin whose installed version is under what this build's bundle expects, unless the operator chose that version.
    public static bool IsBelowBundledMinimum(string installed, string? expectedMin, bool versionChosen) =>
        !versionChosen && expectedMin is not null && IsNewer(expectedMin, installed);

    public static bool IsNewer(string candidate, string current)
    {
        if (System.Version.TryParse(candidate, out var candidateVersion) && System.Version.TryParse(current, out var currentVersion))
        {
            return candidateVersion > currentVersion;
        }

        return !string.Equals(candidate?.Trim(), current?.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
