using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Cockpit.TestSupport;

/// <summary>
/// What a rendered window actually paints: every colour it puts on screen, named after the theme token that
/// carries that value, plus every corner radius it draws.
/// </summary>
/// <remarks>
/// <para>
/// This is the other half of <c>ThemeHexColorGuardTests</c>, which reads the source and says of itself that it is
/// "a tripwire, not a proof". A colour that never appears as a hex literal walks straight past a source lint, and
/// AC-337 found four shapes of exactly that at once: a fallback beside a lookup still holding the old orange, a
/// literal that named no token at all, a lookup on a token that has never existed, and named framework colours
/// (<c>Brushes.Coral</c>). Reading the rendered tree names nothing, so all four arrive here the same way — as a
/// colour on screen that no token accounts for.
/// </para>
/// <para>
/// <b>No bounds and no text, deliberately.</b> This repo embeds no fonts — <c>Assets/</c> holds the app icon and
/// nothing else, and <c>Program.CockpitFontOptions</c> adds a different emoji fallback per platform — so glyph
/// metrics, and every layout measured from them, differ between a developer's machine and CI's Linux runner.
/// Colour and corner radius do not.
/// </para>
/// <para>
/// <b>No counts, deliberately.</b> A tally moves the moment a row is added, so a baseline carrying one would
/// redden on edits that changed no colour at all — and a baseline that cries wolf is one people learn to
/// overwrite unread.
/// </para>
/// <para>
/// Two things it cannot see: a popup is its own visual root, so an open dropdown or flyout is not reached from
/// the window (the ticket lists those as live-only anyway), and a gradient has no single colour to record.
/// </para>
/// </remarks>
public static partial class ThemePalette
{
    private const string OffPalette = "off-palette";

    /// <summary>The palette of a shown window, as the text a baseline is compared against.</summary>
    public static string Describe(Visual root)
    {
        var colours = new HashSet<Color>();
        var radii = new HashSet<CornerRadius>();
        _Collect(root, colours, radii);

        var tokens = _TokensByColour();
        var report = new StringBuilder();

        report.AppendLine("# colours — the theme token holding each value, or off-palette when none does");
        foreach (var colour in colours.OrderBy(Hex, StringComparer.Ordinal))
        {
            report.AppendLine($"{Hex(colour)}  {(tokens.TryGetValue(colour, out var names) ? names : OffPalette)}");
        }

        report.AppendLine();
        report.AppendLine("# corner radii");
        foreach (var radius in radii.OrderBy(corner => corner.TopLeft).ThenBy(corner => corner.ToString(), StringComparer.Ordinal))
        {
            // Prefixed, so every line that carries a claim says what kind it is and the two sections can be
            // compared as one set.
            report.AppendLine($"radius  {radius}");
        }

        return report.ToString();
    }

    /// <summary>
    /// Walks what is drawn. It stops at <see cref="Visual.IsVisible"/> — a hidden branch paints nothing — but
    /// deliberately not at zero-sized nodes, the way <c>VisualTreeSnapshot</c> does: a size comes out of
    /// measurement, measurement comes out of the fonts, and that is the machine-dependence this whole file exists
    /// to stay clear of.
    /// </summary>
    private static void _Collect(Visual visual, HashSet<Color> colours, HashSet<CornerRadius> radii)
    {
        if (!visual.IsVisible)
        {
            return;
        }

        // Not an if/else chain: a Border is a Decorator and a TextBlock is a Control, so a node matches at most
        // one of these — but each carries its own set of painted properties and none subsumes another.
        if (visual is Border border)
        {
            _Add(colours, border.Background);
            _Add(colours, border.BorderBrush);
            radii.Add(border.CornerRadius);
        }

        if (visual is TemplatedControl templated)
        {
            _Add(colours, templated.Background);
            _Add(colours, templated.BorderBrush);
            _Add(colours, templated.Foreground);
            radii.Add(templated.CornerRadius);
        }

        if (visual is TextBlock text)
        {
            _Add(colours, text.Foreground);
        }

        if (visual is Panel panel)
        {
            _Add(colours, panel.Background);
        }

        if (visual is Shape shape)
        {
            _Add(colours, shape.Fill);
            _Add(colours, shape.Stroke);
        }

        foreach (var child in visual.GetVisualChildren())
        {
            _Collect(child, colours, radii);
        }
    }

    /// <summary>
    /// A brush's colour, when it has one. Fully transparent is skipped rather than recorded: it is how this app
    /// spells "no fill", and every such brush would otherwise arrive as the same meaningless entry.
    /// </summary>
    private static void _Add(HashSet<Color> colours, IBrush? brush)
    {
        if (brush is ISolidColorBrush solid && solid.Color.A > 0)
        {
            colours.Add(solid.Color);
        }
    }

    /// <summary>
    /// Every colour token, keyed by the value it resolves to. The names come from <c>Theme.axaml</c> but the
    /// values come from the running application, so what a report calls a colour is what the app actually handed
    /// out — and a key the app cannot resolve fails here rather than quietly leaving colours unnamed. Several
    /// names can share one value (<c>CockpitTextOnStatusColor</c> and <c>CockpitWindowBgColor</c> are the same
    /// near-black today, on purpose and for different reasons), so a value carries all of them.
    /// </summary>
    private static IReadOnlyDictionary<Color, string> _TokensByColour()
    {
        var application = Application.Current
            ?? throw new InvalidOperationException("No application is running, so no theme token can be resolved.");

        var namesByColour = new Dictionary<Color, SortedSet<string>>();
        foreach (var key in _TokenKeys())
        {
            if (!application.TryFindResource(key, out var value) || value is not Color colour)
            {
                throw new InvalidOperationException(
                    $"Theme.axaml declares the colour token '{key}', but the running application does not resolve it to a colour.");
            }

            if (!namesByColour.TryGetValue(colour, out var names))
            {
                names = new SortedSet<string>(StringComparer.Ordinal);
                namesByColour[colour] = names;
            }

            names.Add(key);
        }

        return namesByColour.ToDictionary(entry => entry.Key, entry => string.Join(", ", entry.Value));
    }

    private static IEnumerable<string> _TokenKeys()
    {
        // Fully qualified: Avalonia's Shape namespace brings its own Path in, and this file needs both.
        var theme = System.IO.Path.Combine(RepositoryPaths.Root, "src", "Cockpit.App", "Styles", "Theme.axaml");
        return ColourToken().Matches(File.ReadAllText(theme)).Select(match => match.Groups["key"].Value);
    }

    /// <summary>Always eight digits, so the ordering a report is written in is the ordering of the text.</summary>
    public static string Hex(Color colour) => $"#{colour.A:X2}{colour.R:X2}{colour.G:X2}{colour.B:X2}";

    [GeneratedRegex("""<Color\s+x:Key="(?<key>[^"]+)"\s*>""")]
    private static partial Regex ColourToken();
}
