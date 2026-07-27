using System.Text.RegularExpressions;
using FluentAssertions;

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
/// would not be a literal match and would slip past. It scans <c>Cockpit.App</c> and <c>Cockpit.Plugins.Abstractions</c>
/// only — the two projects AC-334 touched, and the only two a plugin author's own code sits beside.
/// </para>
/// </summary>
public partial class ThemeHexColorGuardTests
{
    /// <summary>
    /// A hex literal outside <c>Theme.axaml</c> that is not a hardcoded colour: an alpha-tinted echo of a token
    /// (the properties that carry transparency — <c>BoxShadow</c>, a translucent <c>Background</c> — have no brush
    /// type to hang a <c>{StaticResource}</c> on) or a plain black scrim/shadow that is deliberately colour-agnostic
    /// — no theme token means "black", so there is nothing for it to point at. Keyed by the file's path relative to
    /// <c>src/</c> plus the exact literal, so a second, different literal landing in the same file does not
    /// silently inherit this file's allowance.
    /// </summary>
    private static readonly Dictionary<(string Path, string Hex), (int Occurrences, string Reason)> AllowedLiterals =
        new()
        {
            [("Cockpit.App/Views/CockpitView.axaml", "#263b82f6")] =
                (1, "26-alpha echo of CockpitAccentColor for the update banner tint"),
            [("Cockpit.App/Views/CockpitView.axaml", "#26E0A33E")] =
                (1, "26-alpha echo of CockpitStatusWaitingColor for the unprotected-secrets banner tint"),
            [("Cockpit.App/Views/CockpitView.axaml", "#40000000")] =
                (1, "black drop-shadow on the resource flyout panel, not tied to any theme colour"),
            [("Cockpit.App/Views/OptionsDialog.axaml", "#CC0f1116")] =
                (2, "CC-alpha echo of CockpitWindowBgColor, shared by the migration and calibration blocking overlays"),
            [("Cockpit.App/Views/VoiceOverlayWindow.axaml", "#F01a1d24")] =
                (1, "F0-alpha echo of CockpitPanelBgColor for the voice pill background"),
            [("Cockpit.App/Views/VoiceOverlayWindow.axaml", "#1AFFFFFF")] =
                (1, "white hairline border at low alpha — colourless, not a theme colour"),
            [("Cockpit.App/Views/VoiceOverlayWindow.axaml", "#2E3b82f6")] =
                (1, "2E-alpha echo of CockpitAccentColor for the listening-dot glow"),
            [("Cockpit.App/Views/ScreenshotSelectionWindow.axaml", "#99000000")] =
                (4, "black screen-dim scrim outside the selection rectangle, not tied to any theme colour"),
            [("Cockpit.App/Controls/ConsentBanner.axaml", "#66000000")] =
                (1, "black drop-shadow, not tied to any theme colour"),
            [("Cockpit.App/Controls/ConsentBannerHost.axaml", "#B3000000")] =
                (1, "black modal scrim, not tied to any theme colour"),
        };

    [Fact]
    public void NoHardcodedColour_OutsideThemeAxaml()
    {
        var srcDirectory = _LocateRepositoryFolder("src")
            ?? throw new InvalidOperationException("No src/ directory above the test output — this test reads the repo it belongs to.");

        var scannedFiles = _SourceFiles(Path.Combine(srcDirectory, "Cockpit.App"))
            .Concat(_SourceFiles(Path.Combine(srcDirectory, "Cockpit.Plugins.Abstractions")))
            .ToList();

        scannedFiles.Should().HaveCountGreaterThan(100,
            "the two projects together have well over a hundred source files — finding almost none means the walk broke, not that the rule holds");

        var found = new Dictionary<(string Path, string Hex), int>();
        foreach (var file in scannedFiles)
        {
            var relativePath = Path.GetRelativePath(srcDirectory, file).Replace(Path.DirectorySeparatorChar, '/');
            if (relativePath == "Cockpit.App/Styles/Theme.axaml")
            {
                continue; // the one file allowed to define the palette itself
            }

            foreach (var line in File.ReadLines(file))
            {
                foreach (var hex in _NonExemptHexMatches(line))
                {
                    var key = (relativePath, hex);
                    found[key] = found.GetValueOrDefault(key) + 1;
                }
            }
        }

        found.Should().ContainKey(("Cockpit.App/Views/OptionsDialog.axaml", "#CC0f1116"),
            "if this known allowed alpha-echo stopped matching, this test would pass for the wrong reason");

        var unexpected = found
            .Where(entry => !AllowedLiterals.TryGetValue(entry.Key, out var allowed) || allowed.Occurrences != entry.Value)
            .Select(entry => $"{entry.Key.Path}: {entry.Key.Hex} ({entry.Value}x)")
            .OrderBy(text => text, StringComparer.Ordinal)
            .ToList();

        unexpected.Should().BeEmpty(
            "a colour belongs in Theme.axaml as a token, or is resolved live through ThemeBrush.Resolve with a " +
            "fallback hex; a literal outside those two either needs a token or, if it is a deliberate alpha-echo " +
            $"or colour-agnostic literal, an entry in {nameof(AllowedLiterals)} with the reason");
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
        var srcDirectory = _LocateRepositoryFolder("src")
            ?? throw new InvalidOperationException("No src/ directory above the test output — this test reads the repo it belongs to.");

        var themeAxamlPath = Path.Combine(srcDirectory, "Cockpit.App", "Styles", "Theme.axaml");
        var themeTokens = _ParseThemeColorTokens(themeAxamlPath);

        themeTokens.Should().HaveCountGreaterThan(10,
            "Theme.axaml defines well over a dozen Color tokens — finding almost none means the parse broke, not that the rule holds");

        var scannedFiles = _SourceFiles(Path.Combine(srcDirectory, "Cockpit.App"))
            .Concat(_SourceFiles(Path.Combine(srcDirectory, "Cockpit.Plugins.Abstractions")))
            .ToList();

        var callSiteCount = 0;
        var mismatches = new List<string>();
        foreach (var file in scannedFiles)
        {
            var relativePath = Path.GetRelativePath(srcDirectory, file).Replace(Path.DirectorySeparatorChar, '/');
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

        callSiteCount.Should().BeGreaterThan(10,
            "MarkdownView, MicLevelMeter, ProviderConfigStatus and ManagedCliConfigSection together call ThemeBrush.Resolve well over a dozen times — finding almost none means the scan broke, not that the rule holds");

        mismatches.Should().BeEmpty(
            "a ThemeBrush.Resolve fallback is dead weight if it silently disagrees with the live token it stands in for");
    }

    /// <summary>
    /// Avalonia's colour parser also accepts the CSS 3- and 4-digit shorthand (<c>#f80</c>, <c>#f80c</c>), so the
    /// hex regex has to catch those too, not only 6/8-digit hex.
    /// </summary>
    [Fact]
    public void HexColorRegex_CatchesCssShorthand()
    {
        HexColorRegex().Matches("Background=\"#f80\"").Should().HaveCount(1);
        HexColorRegex().Matches("Background=\"#f80c\"").Should().HaveCount(1);
        HexColorRegex().Matches("Background=\"#3b82f6\"").Should().HaveCount(1);
        HexColorRegex().Matches("Background=\"#263b82f6\"").Should().HaveCount(1);
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

        _NonExemptHexMatches(line).Should().Equal("#abcdef");
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

    [GeneratedRegex("""ThemeBrush\.Resolve\(\s*"(?<key>[^"]+)"\s*,\s*"(?<hex>#[0-9A-Fa-f]{3,8})"\s*\)""")]
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

    private static string? _LocateRepositoryFolder(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
