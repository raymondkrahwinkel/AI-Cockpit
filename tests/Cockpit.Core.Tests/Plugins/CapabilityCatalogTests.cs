using System.Reflection;
using System.Text;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Capabilities;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>
/// AC-474, criteria 2 and 4: the catalogue against the SDK surface it describes, and against the table in
/// docs/plugins/API-REFERENCE.md. Neither can move without the other.
/// </summary>
public class CapabilityCatalogTests
{
    private static readonly Type[] _Surface = [typeof(ICockpitHost), typeof(ICockpitActions), typeof(IPluginStorage)];

    // The two members that are doors rather than contribution points: they hand back an interface whose own
    // members are catalogued individually, and reaching them does nothing on its own. ICockpitHost.Services is
    // deliberately NOT here — resolving the host's internals is the one accessor that is itself a capability.
    private static readonly HashSet<string> _Doors = ["ICockpitHost.Actions", "ICockpitHost.Storage"];

    [Fact]
    public void EveryContributionPointIsInACapability()
    {
        var catalogued = CapabilityCatalog.All
            .SelectMany(capability => capability.ContributionPoints)
            .ToHashSet(StringComparer.Ordinal);

        var surface = _ContributionPoints().ToHashSet(StringComparer.Ordinal);

        // Both directions. Only the first is what the criterion asks for, but without the second a rename leaves
        // the catalogue pointing at a member that no longer exists and the suite stays green.
        Assert.Empty(surface.Except(catalogued).Order());
        Assert.Empty(catalogued.Except(surface).Order());

        // A sweep that found nothing would pass forever while proving nothing.
        Assert.NotEmpty(surface);
        Assert.Equal(CapabilityCatalog.All.Count, CapabilityCatalog.All.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count());

        // A contribution point in two capabilities means two grants for one call, which the grant broker
        // cannot resolve — the catalogue has to partition the surface, not merely cover it.
        var shared = CapabilityCatalog.All
            .SelectMany(capability => capability.ContributionPoints)
            .GroupBy(point => point, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .Order();

        Assert.Empty(shared);
    }

    [Fact]
    public void TheDocumentedTableMatchesTheCatalogue()
    {
        var reference = _ApiReference();
        var expected = _ExpectedTable();

        Assert.Contains(expected, reference, StringComparison.Ordinal);
    }

    private static IEnumerable<string> _ContributionPoints()
    {
        foreach (var contract in _Surface)
        {
            foreach (var member in contract.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                // Property getters and event add/remove pairs arrive as methods too; the property or event
                // itself is already in this list, so counting the accessors would double every one of them.
                if (member is MethodInfo { IsSpecialName: true })
                {
                    continue;
                }

                var point = $"{contract.Name}.{member.Name}";

                if (!_Doors.Contains(point))
                {
                    yield return point;
                }
            }
        }
    }

    private static string _ExpectedTable()
    {
        var table = new StringBuilder()
            .AppendLine("| ID | Capability | Risk | Since | Scope | Contribution points |")
            .AppendLine("|---|---|---|---|---|---|");

        foreach (var capability in CapabilityCatalog.All)
        {
            var scope = capability.Scope.Count == 0
                ? "—"
                : string.Join(", ", capability.Scope.Select(field => $"`{field.Key}`"));

            table.AppendLine(
                $"| `{capability.Id}` | {capability.Title} | {capability.Risk} | {capability.SinceHostVersion} | {scope} | "
                + string.Join(", ", capability.ContributionPoints.Select(point => $"`{point}`"))
                + " |");
        }

        return table.ToString().ReplaceLineEndings("\n");
    }

    private static string _ApiReference()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        // A worktree's `.git` is a file rather than a directory, and this suite runs from one often enough
        // that only checking for the directory would quietly find nothing and pass.
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Cockpit.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        return File.ReadAllText(Path.Combine(directory.FullName, "docs", "plugins", "API-REFERENCE.md"))
            .ReplaceLineEndings("\n");
    }
}
