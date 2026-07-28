namespace Cockpit.TestSupport;

/// <summary>
/// Holds what a screen paints against the file that records it. Shared by the host's view tests and by each plugin
/// test project that can render, because the rule and the way it is re-recorded have to be the same everywhere —
/// there is one of these, not one per caller.
/// </summary>
public static class ThemePaletteBaseline
{
    private const string RewriteVariable = "COCKPIT_UPDATE_THEME_BASELINES";

    /// <summary>The tail every baseline file's name ends in — how <see cref="VerifyNoOrphans"/> reads a scene name back off one.</summary>
    private const string BaselineSuffix = ".palette.txt";

    /// <summary>
    /// Where a scene's baseline lives. Callers go through this rather than spelling the file name themselves,
    /// because the name is read back apart by <see cref="VerifyNoOrphans"/> and a second copy of the format is a
    /// second thing to keep in step. When they were separate, changing the suffix here left every caller writing
    /// the old name and the orphan check enumerating nothing — which is not an error, it is an empty list, so the
    /// check went green with real orphans sitting in the directory.
    /// </summary>
    public static string PathFor(string baselineDirectory, string scene) =>
        Path.Combine(baselineDirectory, $"{scene}{BaselineSuffix}");

    /// <summary>
    /// Fails when a screen paints a colour or a radius its baseline does not list.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One direction only, and this is the load-bearing decision.</b> It asks whether anything new appeared, not
    /// whether everything is still there. The set of controls a screen renders is not the same on every machine —
    /// a scroll bar exists only when its content overflows, and overflow is decided by text measurement, which this
    /// repo leaves to the operating system's fonts. CI proved it rather than theory: on Linux the Manage-stores
    /// dialog fits, so its scroll bar and the grey Fluent thumb never render, and the Debug tab came back one
    /// Fluent chrome colour short. Both would fail an equality check for having painted *less*, which is not a
    /// regression and not something anyone can act on.
    /// </para>
    /// <para>
    /// What that costs: a colour going missing is not caught. What it keeps is the question the baseline exists to
    /// answer — has this screen started painting something no theme token accounts for — and that one survives,
    /// because a token whose value moves arrives here as a value the file has never seen.
    /// </para>
    /// <para>
    /// Re-recording merges rather than replaces, for the same reason: a run on one machine cannot see the entries
    /// another machine's run produced, and writing the file whole would drop them.
    /// </para>
    /// </remarks>
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

    /// <summary>
    /// Fails when a directory holds a baseline no scene asks for any more (AC-414).
    /// </summary>
    /// <remarks>
    /// The per-scene check above can only look at scenes that still exist, so it goes green on a file whose scene
    /// was renamed or deleted — the file simply stops being read, and a baseline nothing is held to is a file that
    /// says a screen is covered when nothing has looked at it since. It is silent by construction: removing a scene
    /// makes the suite *smaller*, which reads as a passing run.
    /// </remarks>
    public static void VerifyNoOrphans(string baselineDirectory, IEnumerable<string> scenes)
    {
        var expected = new HashSet<string>(scenes, StringComparer.Ordinal);

        var orphans = Directory.EnumerateFiles(baselineDirectory, $"*{BaselineSuffix}")
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(file => !expected.Contains(file[..^BaselineSuffix.Length]))
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

    /// <summary>
    /// The lines that carry a claim. Blank lines and comments are there for whoever reads the file and say nothing
    /// about what was painted, so they take no part in the comparison. A comment is a hash <em>followed by a
    /// space</em>: a colour is written <c>#AARRGGBB</c> with none, and reading the two the same way would drop
    /// every colour in the file.
    /// </summary>
    private static HashSet<string> _Entries(string report) =>
        [.. report
            .Split('\n')
            .Select(line => line.Trim('\r', ' '))
            .Where(line => line.Length > 0 && !line.StartsWith("# ", StringComparison.Ordinal))];
}
