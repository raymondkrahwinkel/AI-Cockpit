namespace Cockpit.TestSupport;

// Shared by the host's view tests and every rendering plugin test project, so rule and re-record are the same everywhere.
public static class ThemePaletteBaseline
{
    private const string RewriteVariable = "COCKPIT_UPDATE_THEME_BASELINES";

    private const string BaselineSuffix = ".palette.txt";

    // One spelling of the file name: a second copy once left VerifyNoOrphans enumerating nothing, so it went green on real orphans.
    public static string PathFor(string baselineDirectory, string scene, string variant) =>
        Path.Combine(baselineDirectory, $"{scene}.{variant.ToLowerInvariant()}{BaselineSuffix}");

    // One direction only: overflow, and so a scroll bar, depends on OS fonts, so painting less on one machine is not a regression.
    public static void Verify(string baselinePath, string painted)
    {
        var recorded = File.Exists(baselinePath) ? _Entries(File.ReadAllText(baselinePath)) : null;

        if (Environment.GetEnvironmentVariable(RewriteVariable) == "1")
        {
            _Rewrite(baselinePath, painted, recorded);

            // A run that rewrites what it is checking must never be able to come out green.
            throw new InvalidOperationException(
                $"Re-recorded {Path.GetFileName(baselinePath)}. Review the diff, then run again without {RewriteVariable}.");
        }

        if (recorded is null)
        {
            throw new InvalidOperationException(
                $"{baselinePath} does not exist, so there is nothing to hold this screen to. "
                + $"Run with {RewriteVariable}=1 to record it, then read what it recorded before committing it.");
        }

        var unknown = _Entries(painted).Except(recorded, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (unknown.Count > 0)
        {
            throw new InvalidOperationException(
                $"{Path.GetFileName(baselinePath)} does not account for what this screen now paints:"
                + $"{Environment.NewLine}  {string.Join($"{Environment.NewLine}  ", unknown)}"
                + $"{Environment.NewLine}Either a colour stopped coming from the theme, or the change is meant — "
                + $"in which case re-record with {RewriteVariable}=1 and review the diff.");
        }
    }

    // AC-414: the per-scene check goes green on a file whose scene was renamed or deleted — a smaller suite reads as a passing run.
    public static void VerifyNoOrphans(string baselineDirectory, IEnumerable<string> scenes)
    {
        var expected = new HashSet<string>(
            scenes.SelectMany(scene => ThemeVariants.Names.Select(variant => Path.GetFileName(PathFor(baselineDirectory, scene, variant)))),
            StringComparer.Ordinal);

        var orphans = Directory.EnumerateFiles(baselineDirectory, $"*{BaselineSuffix}")
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(file => !expected.Contains(file))
            .Order(StringComparer.Ordinal)
            .ToList();

        if (orphans.Count > 0)
        {
            throw new InvalidOperationException(
                $"{baselineDirectory} holds baselines no scene asks for:"
                + $"{Environment.NewLine}  {string.Join($"{Environment.NewLine}  ", orphans)}"
                + $"{Environment.NewLine}A scene was renamed or removed and its file stayed behind. Delete the file, "
                + "or put the scene back if it was not meant to go.");
        }
    }

    private static void _Rewrite(string baselinePath, string painted, IReadOnlySet<string>? recorded)
    {
        var directory = Path.GetDirectoryName(baselinePath)
            ?? throw new InvalidOperationException($"'{baselinePath}' has no directory to write the baseline into.");

        Directory.CreateDirectory(directory);

        var merged = _Entries(painted);
        if (recorded is not null)
        {
            merged.UnionWith(recorded);
        }

        // A header, because this file is read in a diff by someone who did not write it and has to decide whether
        // a new line is a repaint or a regression.
        string[] header =
        [
            "# What this screen paints: every colour, named after the theme token holding that value, or",
            "# off-palette when no token does — plus every corner radius. A screen may paint fewer of these",
            "# than are listed (a scroll bar exists only when its content overflows); it may not paint one",
            $"# that is missing. Re-record with {RewriteVariable}=1 and read the diff.",
            string.Empty,
        ];

        File.WriteAllLines(baselinePath, [.. header, .. merged.Order(StringComparer.Ordinal)]);
    }

    private static HashSet<string> _Entries(string report) =>
        [.. report
            .Split('\n')
            .Select(line => line.Trim('\r', ' '))
            .Where(line => line.Length > 0 && !line.StartsWith("# ", StringComparison.Ordinal))];
}
