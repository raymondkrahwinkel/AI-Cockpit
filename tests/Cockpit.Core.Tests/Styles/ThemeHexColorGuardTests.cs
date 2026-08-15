using System.Text.RegularExpressions;
using Cockpit.TestSupport;

namespace Cockpit.Core.Tests.Styles;

/// <summary>
/// A UI colour lives in one place: <c>Theme.axaml</c> (AC-334). Every hardcoded hex literal that used to sit in a
/// view, a control or the plugin SDK's shared widgets was replaced by a token lookup — either a static
/// <c>{StaticResource ...}</c> in markup, or a live <c>ThemeBrush.Resolve(key, fallbackHex)</c> call for the few
/// spots that build their visuals in code (<c>MicLevelMeter</c>'s custom render, <c>MarkdownView</c>, and the SDK's
/// <c>ProviderConfigStatus</c>/<c>ManagedCliConfigSection</c>, which keep their own tiny copy of the same helper
/// rather than referencing <c>Cockpit.App</c>'s — sharing it would make the helper public SDK API). This guards
/// the regression: a new literal hex slipping back in instead of going through either of those two.
/// <para>
/// Reading the source rather than the compiled app, the way <c>ExternalLinkSingleSourceTests</c> does it: a literal
/// like <c>Background="#123456"</c> is text in the .axaml/.cs source and is not reliably recoverable once Avalonia
/// has folded it into a compiled resource tree.
/// </para>
/// <para>
/// It is a tripwire, not a proof: a hex built from string concatenation, or read from a constant defined elsewhere,
/// would not be a literal match and would slip past.
/// </para>
/// <para>
/// Three spellings of a hardcoded colour are covered: a quoted hex literal, <c>Color.FromRgb</c>/<c>FromArgb</c>
/// components, and — since AC-402 — a named framework colour, <c>Brushes.X</c>/<c>Colors.X</c>, except
/// <c>Transparent</c>, which names the absence of a colour rather than one of the cockpit's own.
/// </para>
/// <para>
/// It scans <c>Cockpit.App</c>, <c>Cockpit.Plugins.Abstractions</c> <b>and every plugin under <c>plugins-dev/</c></b>
/// (AC-337). The plugins were outside it until the repaint reached them, and that is exactly where the drift had
/// collected: fallbacks still holding the pre-AC-334 orange, and one naming a <c>CockpitTextBrush</c> that has never
/// existed — a lookup that could only ever return its literal. A plugin is an independently published artifact and
/// keeps its own copy of the tiny <c>_Brush</c> helper rather than sharing one, so this guard matches on the call
/// <em>shape</em>, not on a shared type. It is a source-tree lint, not a runtime coupling: it reads files, so it
/// lives once here instead of being copied into ten plugin test projects.
/// </para>
/// </summary>
public partial class ThemeHexColorGuardTests
{
    /// <summary>
    /// A hex literal outside <c>Theme.axaml</c> that is not a hardcoded colour: an alpha-tinted echo of a token
    /// (the properties that carry transparency — <c>BoxShadow</c>, a translucent <c>Background</c> — have no brush
    /// type to hang a <c>{StaticResource}</c> on) or a plain black scrim/shadow that is deliberately colour-agnostic
    /// — no theme token means "black", so there is nothing for it to point at. Keyed by the file's path relative to
    /// the repository root plus the exact literal, so a second, different literal landing in the same file does not
    /// silently inherit this file's allowance.
    /// </summary>
    private static readonly Dictionary<(string Path, string Hex), (int Occurrences, string Reason)> AllowedLiterals =
        new()
        {
            [("src/Cockpit.App/Views/CockpitView.axaml", "#263b82f6")] =
                (1, "26-alpha echo of CockpitAccentColor for the update banner tint"),
            [("src/Cockpit.App/Views/CockpitView.axaml", "#26E0A33E")] =
                (1, "26-alpha echo of CockpitStatusWaitingColor for the unprotected-secrets banner tint"),
            [("src/Cockpit.App/Styles/Theme.axaml", "#2AE0A33E")] =
                (1, "2A-alpha echo of CockpitStatusWaitingColor for the needs-attention sidebar row (AC-406) — " +
                     "replaces a pre-mixed opaque #2E2A26 that would have held the old waiting colour through a " +
                     "repaint; the alpha was picked by rendering the row against CockpitSecondaryBgColor, the " +
                     "sidebar's real background, and matching the previous pixels"),
            [("src/Cockpit.App/Views/CockpitView.axaml", "#40000000")] =
                (1, "black drop-shadow on the resource flyout panel, not tied to any theme colour"),
            [("src/Cockpit.App/Views/OptionsDialog.axaml", "#CC0f1116")] =
                (2, "CC-alpha echo of CockpitWindowBgColor, shared by the migration and calibration blocking overlays"),
            [("src/Cockpit.App/Views/VoiceOverlayWindow.axaml", "#F01a1d24")] =
                (1, "F0-alpha echo of CockpitPanelBgColor for the voice pill background"),
            [("src/Cockpit.App/Views/VoiceOverlayWindow.axaml", "#1AFFFFFF")] =
                (1, "white hairline border at low alpha — colourless, not a theme colour"),
            [("src/Cockpit.App/Views/VoiceOverlayWindow.axaml", "#2E3b82f6")] =
                (1, "2E-alpha echo of CockpitAccentColor for the listening-dot glow"),
            [("src/Cockpit.App/Views/ScreenshotSelectionWindow.axaml", "#99000000")] =
                (4, "black screen-dim scrim outside the selection rectangle, not tied to any theme colour"),
            [("src/Cockpit.App/Controls/ConsentBanner.axaml", "#66000000")] =
                (1, "black drop-shadow, not tied to any theme colour"),
            [("src/Cockpit.App/Controls/ConsentBannerHost.axaml", "#B3000000")] =
                (1, "black modal scrim, not tied to any theme colour"),
            [("plugins-dev/Cockpit.Plugin.Workflows/Canvas/NodeDialog.cs", "#B0000000")] =
                (1, "black modal scrim behind the node dialog, not tied to any theme colour"),
            // The canvas's two non-accent kind stripes. A categorical palette, like the usage chart's: their only
            // job is to be told apart from each other and from the trigger's accent. Pointing them at status
            // tokens would give a decision node a colour this app reads as "blocked" — and the card's border is
            // already the channel that carries run status, so the two would contradict each other on one card.
            [("plugins-dev/Cockpit.Plugin.Workflows/Canvas/WorkflowNodeControl.cs", "#C79A4A")] =
                (1, "the decision node's kind stripe — a categorical colour, not a status"),
            [("plugins-dev/Cockpit.Plugin.Workflows/Canvas/WorkflowNodeControl.cs", "#7A8290")] =
                (1, "the plain step's kind stripe — a neutral slate, deliberately hueless so it cannot be read as a faded accent"),
        };

    /// <summary>
    /// Files whose hex literals are not the cockpit's colour at all, because they are not drawing the cockpit.
    /// Listed whole rather than literal by literal: everything in them is picture, so a per-literal allowance
    /// would be the same reason repeated and would break on every edit to the picture.
    /// </summary>
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

        // The diagram plugin's sample render (AC-809): a fixed palette for Mermaider, not the cockpit's UI chrome.
        "plugins-dev/Cockpit.Plugin.Diagram/DiagramWorkspaceBody.cs",
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

        Assert.Contains(("src/Cockpit.App/Views/OptionsDialog.axaml", "#CC0f1116"), found.Keys);

        var unexpected = found
            .Where(entry => !AllowedLiterals.TryGetValue(entry.Key, out var allowed) || allowed.Occurrences != entry.Value)
            .Select(entry => $"{entry.Key.Path}: {entry.Key.Hex} ({entry.Value}x)")
            .OrderBy(text => text, StringComparer.Ordinal)
            .ToList();

        Assert.Empty(unexpected);
    }

    /// <summary>
    /// Every <c>ThemeBrush.Resolve("XxxBrush", "#hex")</c> fallback must equal the value of <c>Theme.axaml</c>'s
    /// matching <c>XxxColor</c> token (compared case-insensitively — the tokens are deliberately mixed-case), so a
    /// future token-value change (another AC-334-style repaint) cannot drift from the fallback that fires for the
    /// few callers that build their visuals outside Avalonia's styling system.
    /// </summary>
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

    /// <summary>
    /// Avalonia's colour parser also accepts the CSS 3- and 4-digit shorthand (<c>#f80</c>, <c>#f80c</c>), so the
    /// hex regex has to catch those too, not only 6/8-digit hex.
    /// </summary>
    [Fact]
    public void HexColorRegex_CatchesCssShorthand()
    {
        Assert.Single(HexColorRegex().Matches("Background=\"#f80\""));
        Assert.Single(HexColorRegex().Matches("Background=\"#f80c\""));
        Assert.Single(HexColorRegex().Matches("Background=\"#3b82f6\""));
        Assert.Single(HexColorRegex().Matches("Background=\"#263b82f6\""));
    }

    /// <summary>
    /// The third spelling (AC-402): <c>Brushes.X</c>/<c>Colors.X</c> is caught regardless of which of the two
    /// static classes it names, but <c>Transparent</c> is not a hardcoded colour and must not match.
    /// </summary>
    [Fact]
    public void NamedFrameworkColorRegex_CatchesBrushesAndColorsButNotTransparent()
    {
        Assert.Single(NamedFrameworkColorRegex().Matches("Foreground = Brushes.Gray"));
        Assert.Single(NamedFrameworkColorRegex().Matches("Foreground = Colors.White"));
        Assert.Empty(NamedFrameworkColorRegex().Matches("Background = Brushes.Transparent"));
    }

    /// <summary>
    /// A named colour is bare code, so — unlike the hex regex, which is saved by the quoted-string check — a
    /// mention in a doc comment (<c>Brushes.Orange</c>, right here in this sentence) would otherwise match. This is
    /// the exact shape <c>ClusterRowControl.cs</c> has: a <c>&lt;c&gt;Brushes.Orange&lt;/c&gt;</c> in its XML doc
    /// remarking on a colour it used to hardcode.
    /// </summary>
    [Fact]
    public void NamedFrameworkColorMatches_IgnoresLineComments()
    {
        Assert.Empty(_NamedFrameworkColorMatches("/// The exec-auth warning used to be drawn in <c>Brushes.Orange</c>."));
        Assert.Equal(
            new[] { "Brushes.Gray" },
            _NamedFrameworkColorMatches("Foreground = Brushes.Gray; // was Brushes.Orange before AC-402"));
    }

    /// <summary>
    /// The <c>ThemeBrush.Resolve(key, fallback)</c> exemption is expression-based, not line-based: a hex on the
    /// same line as a legitimate call — but not itself that call's fallback argument — must still be caught. This
    /// is the shape <c>MicLevelMeter.cs</c> already has (two <c>Resolve</c> calls sharing one line); the assertion
    /// here is what would have caught a stray literal smuggled onto that same line.
    /// </summary>
    [Fact]
    public void NonExemptHexMatches_CatchesStrayLiteralOnAResolveLine()
    {
        const string line =
            """var fill = level >= threshold ? ThemeBrush.Resolve("CockpitAccentBrush", "#3b82f6") : new SolidColorBrush(Color.Parse("#abcdef"));""";

        Assert.Equal(new[] { "#abcdef" }, _NonExemptHexMatches(line));
    }

    /// <summary>
    /// A plugin resolves through its own copy of the helper — <c>_Brush("key", "#hex")</c> — because a plugin is an
    /// independently published artifact and does not share a type with the host for this. The exemption therefore
    /// matches the call shape, and it has to reach that copy as well as the host's <c>ThemeBrush.Resolve</c>; a
    /// literal that is not anyone's fallback argument still has to be caught on the same line.
    /// </summary>
    [Fact]
    public void NonExemptHexMatches_ReachesThePluginsOwnBrushHelper()
    {
        const string line =
            """DiffLineKind.Added => _Brush("CockpitStatusDoneBrush", "#5AA576"), DiffLineKind.Hunk => new SolidColorBrush(Color.Parse("#5A9BD4")),""";

        Assert.Equal(new[] { "#5A9BD4" }, _NonExemptHexMatches(line));
    }

    /// <summary>
    /// The mismatch this whole guard exists for, asserted directly rather than only through the repo walk: a key
    /// that names no token at all reports, instead of passing because there was nothing to compare against. This is
    /// what session-review's <c>CockpitTextBrush</c> was doing — a lookup that could only ever return its literal.
    /// </summary>
    [Fact]
    public void ResolveCallRegex_MatchesBothHelperShapes()
    {
        Assert.Single(ResolveCallRegex().Matches("""ThemeBrush.Resolve("CockpitAccentBrush", "#3b82f6")"""));
        Assert.Single(ResolveCallRegex().Matches("""_Brush("CockpitAccentBrush", "#3b82f6")"""));
        Assert.Single(ResolveCallRegex().Matches("""Brush("CockpitAccentBrush", "#3b82f6")"""));
    }

    /// <summary>
    /// Filters the hex literals on one source line down to the ones that are an actual hardcoded colour: a match
    /// has to sit inside a quoted string (a bare <c>#34b</c> ticket reference in a <c>///</c>/<c>&lt;!-- --&gt;</c>
    /// comment is prose, not a colour — see <c>SessionPanelViewModel</c>'s "(#35b)"), and it must not be the second,
    /// quoted argument of a <c>ThemeBrush.Resolve("key", "fallback")</c> call — the one sanctioned fallback pattern.
    /// </summary>
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

    /// <summary>
    /// Unlike a hex literal, a named framework colour is bare code — <c>Brushes.Gray</c>, no quotes — so it cannot
    /// be told from prose by "is it inside a string". It can be told apart by comment position instead: this
    /// codebase's doc comments are the reason a name like this shows up in a sentence at all (see this file's own
    /// <c>NamedFrameworkColorRegex</c> doc), so anything from the first non-string <c>//</c> onward is prose, not
    /// code.
    /// </summary>
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
        var tokens = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in ThemeColorTokenRegex().Matches(File.ReadAllText(themeAxamlPath)))
        {
            tokens[match.Groups["key"].Value] = match.Groups["hex"].Value;
        }

        return tokens;
    }

    [GeneratedRegex(@"#(?:[0-9A-Fa-f]{8}|[0-9A-Fa-f]{6}|[0-9A-Fa-f]{4}|[0-9A-Fa-f]{3})\b")]
    private static partial Regex HexColorRegex();

    /// <summary>
    /// The same violation spelled in components rather than in hex: <c>Color.FromRgb(0x1A, 0x12, 0x0E)</c>. Autopilot
    /// held nine of these — inks mixed for the pre-AC-334 orange, sitting on fills that had since moved — and the
    /// hex-only rule could not see a single one. There is no sanctioned fallback form here: a fallback is written as
    /// a hex string, so anything reaching this regex is a hardcoded colour or an exempt picture.
    /// </summary>
    [GeneratedRegex(@"Color\.From(?:Rgb|Argb)\s*\([^)]*\)")]
    private static partial Regex ColorFromComponentsRegex();

    /// <summary>
    /// The third spelling (AC-402): a named framework colour, <c>Brushes.X</c> or <c>Colors.X</c>, used directly
    /// instead of a theme token. <c>Transparent</c> is excluded — it names the absence of a colour, not one of the
    /// cockpit's own, and both <c>StatusBrushConverter</c> and this file's own <c>_Brush</c>/<c>Brush</c> callers
    /// legitimately fall back to it.
    /// </summary>
    [GeneratedRegex(@"\b(?:Brushes|Colors)\.(?!Transparent\b)[A-Za-z]+\b")]
    private static partial Regex NamedFrameworkColorRegex();

    /// <summary>
    /// Both shapes of the one sanctioned fallback: the host's <c>ThemeBrush.Resolve(key, hex)</c> and the copy a
    /// plugin keeps for itself, <c>_Brush(key, hex)</c> / <c>Brush(key, hex)</c>.
    /// </summary>
    [GeneratedRegex("""(?:ThemeBrush\.Resolve|\b_?Brush)\(\s*"(?<key>[^"]+)"\s*,\s*"(?<hex>#[0-9A-Fa-f]{3,8})"\s*\)""")]
    private static partial Regex ResolveCallRegex();

    [GeneratedRegex("""<Color x:Key="(?<key>[^"]+)">(?<hex>#[0-9A-Fa-f]{3,8})</Color>""")]
    private static partial Regex ThemeColorTokenRegex();

    [GeneratedRegex("\"[^\"]*\"")]
    private static partial Regex QuotedSpanRegex();

    private static IEnumerable<string> _SourceFiles(string directory) =>
        Directory.EnumerateFiles(directory, "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    /// <summary>
    /// Everything the palette rule covers: the two host projects AC-334 repainted, plus every plugin AC-337 brought
    /// in. Test projects are left out — a fixture may well name a colour to assert on one.
    /// </summary>
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

    /// <summary>
    /// The repository this test belongs to. Shared with the theme baseline in the view tests, which reads the same
    /// tree from a different assembly — the second copy was written and then removed the same day.
    /// </summary>
    private static string _LocateRepositoryRoot() => RepositoryPaths.Root;
}
