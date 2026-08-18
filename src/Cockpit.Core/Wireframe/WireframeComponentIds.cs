using Cockpit.Core.Wireframe.Model;

namespace Cockpit.Core.Wireframe;

// AC-906: where a component's stable id comes from. Minted only when something is about to name a component — an
// agent reading the surface, or the operator taking one under their hand — so a wireframe nobody points at keeps
// the plain text it was written as.
public static class WireframeComponentIds
{
    // Stamps an id on every component that carries none, rewriting only those lines so the indentation and quoting
    // the operator chose survive it. A source with no readable screen comes back untouched — nothing to name yet.
    public static string Ensure(string source)
    {
        var screens = WireframeParser.Parse(source).Screens;
        if (screens.Count == 0)
        {
            return source;
        }

        var components = screens.SelectMany(_Flatten).ToList();
        var missing = components.Where(component => component.Id is null).ToList();
        if (missing.Count == 0)
        {
            return source;
        }

        var taken = components.Where(component => component.Id is not null)
            .Select(component => component.Id!)
            .ToHashSet(StringComparer.Ordinal);
        var lines = source.ReplaceLineEndings("\n").Split('\n');
        var number = 1;
        foreach (var component in missing)
        {
            string id;
            do
            {
                id = $"c{number++}";
            }
            while (!taken.Add(id));

            lines[component.Line - 1] = lines[component.Line - 1].TrimEnd() + $" #{id}";
        }

        return string.Join("\n", lines);
    }

    private static IEnumerable<WireframeNode> _Flatten(WireframeNode node) =>
        new[] { node }.Concat(node.Children.SelectMany(_Flatten));
}
