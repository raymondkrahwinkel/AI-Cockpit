namespace Cockpit.Core.Plugins;

/// <summary>
/// Compares two plugin version strings for update detection (#14). Numeric versions (<c>1.2.0</c>) compare
/// by <see cref="System.Version"/>; anything that does not parse falls back to a plain inequality, so a
/// non-numeric bump (<c>1.2.0-beta</c> → <c>1.2.0</c>) still surfaces as an available update.
/// </summary>
public static class PluginVersion
{
    public static bool IsNewer(string candidate, string current)
    {
        if (System.Version.TryParse(candidate, out var candidateVersion) && System.Version.TryParse(current, out var currentVersion))
        {
            return candidateVersion > currentVersion;
        }

        return !string.Equals(candidate?.Trim(), current?.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
