namespace Cockpit.Core.Profiles;

/// <summary>
/// Pure "pick the newest version folder" logic for locating the Claude desktop app's bundled
/// <c>claude</c> executable under <c>%APPDATA%\Claude\claude-code\&lt;version&gt;\</c>. Kept
/// free of any real filesystem-root assumptions so it can be exercised against a temporary
/// fake directory tree in tests.
/// </summary>
public static class BundledClaudeExecutableSelector
{
    /// <summary>
    /// Given the version-folder names directly under <c>claude-code\</c> and a way to check
    /// whether the executable exists inside a given version folder, returns the full
    /// executable path in the highest parseable version folder, or <see langword="null"/> if
    /// none qualify.
    /// </summary>
    public static string? SelectNewestExecutable(
        string claudeCodeRoot,
        IEnumerable<string> versionFolderNames,
        string executableFileName,
        Func<string, bool> fileExists)
    {
        var newestVersionFolder = versionFolderNames
            .Select(name => (Name: name, Version: TryParseVersion(name)))
            .Where(entry => entry.Version is not null)
            .OrderByDescending(entry => entry.Version)
            .Select(entry => entry.Name)
            .FirstOrDefault();

        if (newestVersionFolder is null)
        {
            return null;
        }

        var executablePath = Path.Combine(claudeCodeRoot, newestVersionFolder, executableFileName);
        return fileExists(executablePath) ? executablePath : null;
    }

    private static Version? TryParseVersion(string folderName) =>
        Version.TryParse(folderName, out var version) ? version : null;
}
