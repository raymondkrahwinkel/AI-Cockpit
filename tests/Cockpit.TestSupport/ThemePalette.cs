using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Cockpit.TestSupport;

// The other half of ThemeHexColorGuardTests: a hex-less colour slips past a source lint, and AC-337 found four shapes of one.
public static partial class ThemePalette
{
    private const string OffPalette = "off-palette";

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

    // Stops at hidden or transparent branches, not at zero size: an invisible one-pixel text box would otherwise tint each baseline.
    private static void _Collect(Visual visual, HashSet<Color> colours, HashSet<CornerRadius> radii)
    {
        if (!visual.IsVisible || visual.Opacity <= 0)
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

    // Fully transparent is skipped: it is how this app spells "no fill", and every such brush would arrive as one meaningless row.
    private static void _Add(HashSet<Color> colours, IBrush? brush)
    {
        if (brush is ISolidColorBrush solid && solid.Color.A > 0)
        {
            colours.Add(solid.Color);
        }
    }

    private static IReadOnlyDictionary<Color, string> _TokensByColour()
    {
        var application = Application.Current
            ?? throw new InvalidOperationException("No application is running, so no theme token can be resolved.");

        var namesByColour = new Dictionary<Color, SortedSet<string>>();
        foreach (var key in _TokenKeys())
        {
            if (!application.TryFindResource(key, application.ActualThemeVariant, out var value) || value is not Color colour)
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

    public static string Hex(Color colour) => $"#{colour.A:X2}{colour.R:X2}{colour.G:X2}{colour.B:X2}";

    [GeneratedRegex("""<Color\s+x:Key="(?<key>[^"]+)"\s*>""")]
    private static partial Regex ColourToken();
}
