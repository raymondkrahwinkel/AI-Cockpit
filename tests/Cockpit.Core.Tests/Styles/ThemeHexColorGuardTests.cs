using System.Text.RegularExpressions;
using Cockpit.TestSupport;

namespace Cockpit.Core.Tests.Styles;

// AC-334: reads the source, not the compiled app — a hex literal is not recoverable once Avalonia folds it into resources.
public partial class ThemeHexColorGuardTests
{
    // Keyed by path plus the exact literal, so a second literal in the same file inherits nothing; echoes are tokens since AC-860.
    private static readonly Dictionary<(string Path, string Hex), (int Occurrences, string Reason)> AllowedLiterals =
        new()
        {
            [("src/Cockpit.App/Views/CockpitView.axaml", "#40000000")] =
                (1, "black drop-shadow on the resource flyout panel, not tied to any theme colour"),
            [("src/Cockpit.App/Views/CockpitView.axaml", "#66000000")] =
                (1, "black drop-shadow on AC-1305's consent notification, the same colourless kind as the one "
                    + "above and heavier because that card floats over a conversation rather than over chrome"),
            [("src/Cockpit.App/Views/ScreenshotSelectionWindow.axaml", "#99000000")] =
                (4, "dims a picture of the operator's screen outside the selection — not a surface of the app, so no variant has a say (AC-860)"),
            [("src/Cockpit.App/Controls/ConsentBanner.axaml", "#66000000")] =
                (1, "black drop-shadow, not tied to any theme colour"),
            // The canvas's two non-accent kind stripes. A categorical palette, like the usage chart's: their only
            // job is to be told apart from each other and from the trigger's accent. Pointing them at status
            // tokens would give a decision node a colour this app reads as "blocked" — and the card's border is
            // already the channel that carries run status, so the two would contradict each other on one card.
            [("plugins-dev/Cockpit.Plugin.Workflows/Canvas/WorkflowNodeControl.cs", "#C79A4A")] =
                (1, "the decision node's kind stripe — a categorical colour, not a status"),
            [("plugins-dev/Cockpit.Plugin.Workflows/Canvas/WorkflowNodeControl.cs", "#7A8290")] =
                (1, "the plain step's kind stripe — a neutral slate, deliberately hueless so it cannot be read as a faded accent"),
        };

    // Listed whole rather than per literal: everything in these files is picture, and a per-literal list breaks on every edit.
    private static readonly HashSet<string> AllowedFiles = new(StringComparer.Ordinal)
    {
        // The stand-in desktop the selection surface is rendered over headless (AC-357). Its colours are the
        // *contents* of a screenshot — somebody else's screen — and pointing them at tokens would make the
        // stand-in follow a repaint of the very app it exists to be independent of. It is a file of its own so
        // this exemption reaches only code that draws a picture: the scene wiring beside it stays guarded, and
        // that is the file the rest of AC-356 will be editing.
        "src/Cockpit.App/StandInDesktop.cs",

        // The inks an operator marks a capture in (AC-375). Same argument one step further along: these do not
        // draw the cockpit either, they draw on a picture that leaves it. A token would make a red arrow already
        // sent to an agent mean whatever the next repaint decides red is. The accent is deliberately not in that
        // file — it is read from the theme at runtime and stays the one colour this app owns.
        "src/Cockpit.App/MarkInk.cs",

        // The usage chart's three series colours (AC-54). Same argument again: this is a picture, and its palette
        // has one job — three lines you can tell apart. The file says in so many words why it does not borrow the
        // theme's status colours: an amber line pointed at CockpitStatusWaitingBrush would read as a warning about
        // the data rather than as "this is the 5h line". A categorical palette is not a theme colour.
        "plugins-dev/Cockpit.Plugin.UsageTrend/UsageTrendChartControl.cs",

        // The whiteboard's own surface (AC-821/AC-822), one named palette since AC-860: white paper, a yellow pencil
        // ink and a blue shape stroke — a whiteboard's content is deliberately not theme-driven, the way a real sheet
        // of paper stays white under any repaint of the app around it.
        "plugins-dev/Cockpit.Plugin.Diagram/Whiteboard/Rendering/WhiteboardPalette.cs",

        // The wireframe sketch's greys (AC-871): a wireframe must read as a sketch, never as a finished design, so
        // no product colour belongs in it. All eight literals are achromatic — no token to point at even in
        // principle — and whole-file by this set's own criterion, since it is one reason repeated.
        "plugins-dev/Cockpit.Plugin.Diagram/Wireframe/Rendering/WireframePalette.cs",
    };

    [Fact]
    public void NoHardcodedColour_OutsideThemeAxaml()
    {
        var repositoryRoot = _LocateRepositoryRoot();
        var scannedFiles = _ScannedFiles(repositoryRoot);

        Assert.True(System.Linq.Enumerable.Count(scannedFiles) > 200,
            "the host projects and the twenty-odd plugins together have well over two hundred source files — finding almost none means the walk broke, not that the rule holds");

        var scannedPaths = scannedFiles
            .Select(file => _RepositoryPath(repositoryRoot, file))
            .ToHashSet(StringComparer.Ordinal);

        Assert.All(AllowedFiles, item => Assert.Contains(item, scannedPaths));

        var found = new Dictionary<(string Path, string Hex), int>();
        foreach (var file in scannedFiles)
        {
            var relativePath = _RepositoryPath(repositoryRoot, file);
            if (AllowedFiles.Contains(relativePath))
            {
                continue; // not the cockpit's colour — see AllowedFiles
            }

            var isThemeAxaml = relativePath == "src/Cockpit.App/Styles/Theme.axaml";
            var insideResourcesBlock = false;
            foreach (var line in File.ReadLines(file))
            {
                if (isThemeAxaml)
                {
                    // Only the <Styles.Resources> dictionary is where a colour is meant to live; a literal in a
                    // Style's own Setter (AC-406's needs-attention tint) is the same drift as anywhere else and
                    // this exemption must not hide it.
                    if (line.Contains("<Styles.Resources>", StringComparison.Ordinal))
                    {
                        insideResourcesBlock = true;
                    }

                    if (line.Contains("</Styles.Resources>", StringComparison.Ordinal))
                    {
                        insideResourcesBlock = false;
                    }

                    if (insideResourcesBlock)
                    {
                        continue; // the token dictionary itself — colours are meant to live here
                    }
                }

                foreach (var hex in _NonExemptHexMatches(line))
                {
                    var key = (relativePath, hex);
                    found[key] = found.GetValueOrDefault(key) + 1;
                }

                foreach (Match component in ColorFromComponentsRegex().Matches(line))
                {
                    var key = (relativePath, component.Value);
                    found[key] = found.GetValueOrDefault(key) + 1;
                }

                foreach (var named in _NamedFrameworkColorMatches(line))
                {
                    var key = (relativePath, named);
                    found[key] = found.GetValueOrDefault(key) + 1;
                }
            }
        }

        // A literal the scan is known to reach: proof the walk and the regex work before the empty-set check below.
        Assert.Contains(("src/Cockpit.App/Views/ScreenshotSelectionWindow.axaml", "#99000000"), found.Keys);

        var unexpected = found
            .Where(entry => !AllowedLiterals.TryGetValue(entry.Key, out var allowed) || allowed.Occurrences != entry.Value)
            .Select(entry => $"{entry.Key.Path}: {entry.Key.Hex} ({entry.Value}x)")
            .OrderBy(text => text, StringComparer.Ordinal)
            .ToList();

        Assert.Empty(unexpected);
    }

    // Compared against the dark value: a fallback fires when no theme is loaded, and dark is what the app starts in (AC-860).
    [Fact]
    public void ResolveFallback_MatchesThemeAxamlColorToken()
    {
        var repositoryRoot = _LocateRepositoryRoot();

        var themeAxamlPath = Path.Combine(repositoryRoot, "src", "Cockpit.App", "Styles", "Theme.axaml");
        var themeTokens = _ParseThemeColorTokens(themeAxamlPath);

        Assert.True(System.Linq.Enumerable.Count(themeTokens) > 10,
            "Theme.axaml defines well over a dozen Color tokens — finding almost none means the parse broke, not that the rule holds");

        var scannedFiles = _ScannedFiles(repositoryRoot);

        var callSiteCount = 0;
        var mismatches = new List<string>();
        foreach (var file in scannedFiles)
        {
            var relativePath = _RepositoryPath(repositoryRoot, file);
            foreach (var line in File.ReadLines(file))
            {
                foreach (Match call in ResolveCallRegex().Matches(line))
                {
                    callSiteCount++;
                    var brushKey = call.Groups["key"].Value;
                    var fallbackHex = call.Groups["hex"].Value;
                    var colorKey = brushKey.EndsWith("Brush", StringComparison.Ordinal)
                        ? string.Concat(brushKey.AsSpan(0, brushKey.Length - "Brush".Length), "Color")
                        : brushKey;

                    if (!themeTokens.TryGetValue(colorKey, out var themeHex))
                    {
                        mismatches.Add($"{relativePath}: {brushKey} fallback {fallbackHex} — no {colorKey} token found in Theme.axaml");
                        continue;
                    }

                    if (!string.Equals(fallbackHex, themeHex, StringComparison.OrdinalIgnoreCase))
                    {
                        mismatches.Add($"{relativePath}: {brushKey} fallback is {fallbackHex}, but Theme.axaml's {colorKey} is {themeHex}");
                    }
                }
            }
        }

        Assert.True(callSiteCount > 30,
            "the host's four code-drawn surfaces plus the plugins' own _Brush copies together resolve well over thirty times — finding almost none means the scan broke, not that the rule holds");

        Assert.Empty(mismatches);
    }

    // Avalonia's colour parser also accepts the CSS 3- and 4-digit shorthand (#f80, #f80c), so the regex has to catch those too.
    [Fact]
    public void HexColorRegex_CatchesCssShorthand()
    {
        Assert.Single(HexColorRegex().Matches("Background=\"#f80\""));
        Assert.Single(HexColorRegex().Matches("Background=\"#f80c\""));
        Assert.Single(HexColorRegex().Matches("Background=\"#3b82f6\""));
        Assert.Single(HexColorRegex().Matches("Background=\"#263b82f6\""));
    }

    // AC-402: Brushes.X and Colors.X are both caught, but Transparent is not a hardcoded colour and must not match.
    [Fact]
    public void NamedFrameworkColorRegex_CatchesBrushesAndColorsButNotTransparent()
    {
        Assert.Single(NamedFrameworkColorRegex().Matches("Foreground = Brushes.Gray"));
        Assert.Single(NamedFrameworkColorRegex().Matches("Foreground = Colors.White"));
        Assert.Empty(NamedFrameworkColorRegex().Matches("Background = Brushes.Transparent"));
    }

    // A named colour is bare code, so a mention in a doc comment would match — the shape ClusterRowControl.cs has in its XML doc.
    [Fact]
    public void NamedFrameworkColorMatches_IgnoresLineComments()
    {
        Assert.Empty(_NamedFrameworkColorMatches("/// The exec-auth warning used to be drawn in <c>Brushes.Orange</c>."));
        Assert.Equal(
            new[] { "Brushes.Gray" },
            _NamedFrameworkColorMatches("Foreground = Brushes.Gray; // was Brushes.Orange before AC-402"));
    }

    // The Resolve exemption is per expression, not per line: a stray literal beside a real call (MicLevelMeter.cs) is still caught.
    [Fact]
    public void NonExemptHexMatches_CatchesStrayLiteralOnAResolveLine()
    {
        const string line =
            """var fill = level >= threshold ? ThemeBrush.Resolve("CockpitAccentBrush", "#3b82f6") : new SolidColorBrush(Color.Parse("#abcdef"));""";

        Assert.Equal(new[] { "#abcdef" }, _NonExemptHexMatches(line));
    }

    // A plugin keeps its own _Brush copy rather than a host type, so the exemption matches the call shape and reaches that copy.
    [Fact]
    public void NonExemptHexMatches_ReachesThePluginsOwnBrushHelper()
    {
        const string line =
            """DiffLineKind.Added => _Brush("CockpitStatusDoneBrush", "#5AA576"), DiffLineKind.Hunk => new SolidColorBrush(Color.Parse("#5A9BD4")),""";

        Assert.Equal(new[] { "#5A9BD4" }, _NonExemptHexMatches(line));
    }

    // Asserted directly: a key naming no token must report, not pass for lack of a comparison — CockpitTextBrush once did.
    [Fact]
    public void ResolveCallRegex_MatchesBothHelperShapes()
    {
        Assert.Single(ResolveCallRegex().Matches("""ThemeBrush.Resolve("CockpitAccentBrush", "#3b82f6")"""));
        Assert.Single(ResolveCallRegex().Matches("""_Brush("CockpitAccentBrush", "#3b82f6")"""));
        Assert.Single(ResolveCallRegex().Matches("""Brush("CockpitAccentBrush", "#3b82f6")"""));
    }

    // A bare #34b in a comment is a ticket reference, not a colour, and Resolve's second argument is the one sanctioned fallback.
    private static IEnumerable<string> _NonExemptHexMatches(string line)
    {
        var quotedSpans = QuotedSpanRegex().Matches(line)
            .Select(match => (match.Index, match.Length))
            .ToList();

        var resolveFallbackSpans = ResolveCallRegex().Matches(line)
            .Select(match => match.Groups["hex"])
            .Select(group => (group.Index, group.Length))
            .ToList();

        foreach (Match match in HexColorRegex().Matches(line))
        {
            var insideQuotes = quotedSpans.Any(span => match.Index >= span.Index && match.Index + match.Length <= span.Index + span.Length);
            if (!insideQuotes)
            {
                continue; // prose — a colour literal always sits inside a quoted string in this codebase
            }

            var isSanctionedFallback = resolveFallbackSpans.Any(span => span.Index == match.Index && span.Length == match.Length);
            if (isSanctionedFallback)
            {
                continue; // the fallback argument of a ThemeBrush.Resolve(key, fallback) call
            }

            yield return match.Value;
        }
    }

    // A named colour is bare code, so quotes cannot tell it from prose; anything from the first non-string // onward is prose.
    private static IEnumerable<string> _NamedFrameworkColorMatches(string line)
    {
        var commentStart = _FindLineCommentStart(line);
        var codeSpan = commentStart < 0 ? line : line[..commentStart];
        return NamedFrameworkColorRegex().Matches(codeSpan).Select(match => match.Value);
    }

    private static int _FindLineCommentStart(string line)
    {
        var quotedSpans = QuotedSpanRegex().Matches(line)
            .Select(match => (match.Index, match.Length))
            .ToList();

        for (var index = 0; index < line.Length - 1; index++)
        {
            if (line[index] != '/' || line[index + 1] != '/')
            {
                continue;
            }

            var insideQuotes = quotedSpans.Any(span => index >= span.Index && index < span.Index + span.Length);
            if (!insideQuotes)
            {
                return index;
            }
        }

        return -1;
    }

    private static Dictionary<string, string> _ParseThemeColorTokens(string themeAxamlPath)
    {
        // The Dark dictionary only: Theme.axaml declares every colour once per variant (AC-860), and a merged read
        // would hand back whichever variant happens to be written last.
        var dark = DarkDictionaryRegex().Match(File.ReadAllText(themeAxamlPath));
        Assert.True(dark.Success, "Theme.axaml no longer carries a ResourceDictionary keyed \"Dark\" — the parse below would read nothing");

        var tokens = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in ThemeColorTokenRegex().Matches(dark.Value))
        {
            tokens[match.Groups["key"].Value] = match.Groups["hex"].Value;
        }

        return tokens;
    }

    [GeneratedRegex(@"#(?:[0-9A-Fa-f]{8}|[0-9A-Fa-f]{6}|[0-9A-Fa-f]{4}|[0-9A-Fa-f]{3})\b")]
    private static partial Regex HexColorRegex();

    // Autopilot held nine of these — inks mixed for the pre-AC-334 orange — and the hex-only rule could not see a single one.
    [GeneratedRegex(@"Color\.From(?:Rgb|Argb)\s*\([^)]*\)")]
    private static partial Regex ColorFromComponentsRegex();

    // AC-402: Transparent is excluded — it names the absence of a colour, and StatusBrushConverter legitimately falls back to it.
    [GeneratedRegex(@"\b(?:Brushes|Colors)\.(?!Transparent\b)[A-Za-z]+\b")]
    private static partial Regex NamedFrameworkColorRegex();

    [GeneratedRegex("""(?:ThemeBrush\.Resolve|\b_?Brush)\(\s*"(?<key>[^"]+)"\s*,\s*"(?<hex>#[0-9A-Fa-f]{3,8})"\s*\)""")]
    private static partial Regex ResolveCallRegex();

    [GeneratedRegex("""<Color x:Key="(?<key>[^"]+)">(?<hex>#[0-9A-Fa-f]{3,8})</Color>""")]
    private static partial Regex ThemeColorTokenRegex();

    [GeneratedRegex("""<ResourceDictionary x:Key="Dark">.*?</ResourceDictionary>""", RegexOptions.Singleline)]
    private static partial Regex DarkDictionaryRegex();

    [GeneratedRegex("\"[^\"]*\"")]
    private static partial Regex QuotedSpanRegex();

    private static IEnumerable<string> _SourceFiles(string directory) =>
        Directory.EnumerateFiles(directory, "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    // Test projects are left out: a fixture may well name a colour to assert on one.
    private static List<string> _ScannedFiles(string repositoryRoot)
    {
        var files = _SourceFiles(Path.Combine(repositoryRoot, "src", "Cockpit.App"))
            .Concat(_SourceFiles(Path.Combine(repositoryRoot, "src", "Cockpit.Plugins.Abstractions")))
            .ToList();

        var pluginsRoot = Path.Combine(repositoryRoot, "plugins-dev");
        foreach (var plugin in Directory.EnumerateDirectories(pluginsRoot).Order(StringComparer.Ordinal))
        {
            if (Path.GetFileName(plugin).EndsWith(".Tests", StringComparison.Ordinal))
            {
                continue;
            }

            files.AddRange(_SourceFiles(plugin));
        }

        return files;
    }

    private static string _RepositoryPath(string repositoryRoot, string file) =>
        Path.GetRelativePath(repositoryRoot, file).Replace(Path.DirectorySeparatorChar, '/');

    private static string _LocateRepositoryRoot() => RepositoryPaths.Root;
}
