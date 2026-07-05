namespace Zyra.Voice.Core.Profiles;

/// <summary>
/// Pure auto-detect logic: given a set of candidate config directories, returns a
/// <see cref="ClaudeProfile"/> for each one that actually exists, labelled from its directory
/// name. Kept free of any real filesystem-root assumptions (no <c>%USERPROFILE%</c> lookup
/// here) so it can be exercised against a temporary fake directory tree in tests.
/// </summary>
public static class ClaudeProfileAutoDetector
{
    /// <summary>
    /// Filters <paramref name="candidateConfigDirs"/> down to the ones that exist on disk and
    /// builds a profile per surviving directory, labelled from its directory name (e.g.
    /// <c>.claude-work</c> → <c>work</c>).
    /// </summary>
    public static IReadOnlyList<ClaudeProfile> Detect(IEnumerable<string> candidateConfigDirs, Func<string, bool> directoryExists)
    {
        var profiles = new List<ClaudeProfile>();

        foreach (var configDir in candidateConfigDirs)
        {
            if (!directoryExists(configDir))
            {
                continue;
            }

            profiles.Add(new ClaudeProfile(LabelFromDirectoryName(configDir), configDir));
        }

        return profiles;
    }

    private static string LabelFromDirectoryName(string configDir)
    {
        var name = Path.GetFileName(configDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(name))
        {
            return configDir;
        }

        // ".claude" -> "default", ".claude-work" -> "work", ".claude-personal" -> "personal".
        var trimmed = name.TrimStart('.');
        const string prefix = "claude-";
        if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return trimmed[prefix.Length..];
        }

        return trimmed.Equals("claude", StringComparison.OrdinalIgnoreCase) ? "default" : trimmed;
    }
}
