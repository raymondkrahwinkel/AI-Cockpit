using Cockpit.TestSupport;

namespace Cockpit.Core.Tests.Configuration;

/// <summary>
/// <c>CockpitBrand</c>'s own name (AC-508/AC-512): the product name and the guide/Depot domains resolve from one
/// file, because AC-167 can still change either and two hand-kept copies of a domain drift apart. This guards the
/// regression a review would otherwise have to catch by eye — a new screen spelling the URL or the name out again
/// instead of reading <see cref="Cockpit.Core.Configuration.CockpitBrand"/>.
/// <para>
/// Reads the source rather than the compiled app, the way <c>ExternalLinkSingleSourceTests</c> and
/// <c>ThemeHexColorGuardTests</c> already do: a literal is text in the .cs/.axaml source and is not reliably
/// recoverable once it has been folded into a compiled string or resource.
/// </para>
/// </summary>
public class CockpitBrandSingleSourceTests
{
    /// <summary>
    /// The one pre-existing exception: <c>AboutDialog.axaml</c>'s <c>Design.DataContext</c> sample data, which the
    /// Avalonia previewer reads and the running app never does — <c>AboutInfo.FromAssembly</c> resolves the real
    /// dialog's name from <c>CockpitProduct.DisplayName</c> instead. Keyed on path so a literal landing anywhere
    /// else does not silently inherit this file's allowance.
    /// </summary>
    private static readonly HashSet<string> AllowedFiles = new(StringComparer.Ordinal)
    {
        "src/Cockpit.App/Views/AboutDialog.axaml",
    };

    private static readonly string[] ProtectedLiterals =
    [
        Cockpit.Core.Configuration.CockpitBrand.ProductName,
        Cockpit.Core.Configuration.CockpitBrand.GuideUrl,
        Cockpit.Core.Configuration.CockpitBrand.DepotUrl,
    ];

    [Fact]
    public void NoLiteralRepeatsCockpitBrand_OutsideCockpitBrandItself()
    {
        var repositoryRoot = RepositoryPaths.Root;
        var scannedFiles = _ScannedFiles(repositoryRoot).ToList();

        Assert.True(scannedFiles.Count > 100,
            "src/Cockpit.App and src/Cockpit.Core together have well over a hundred source files — finding almost none means the walk broke, not that the rule holds");

        var found = new List<string>();
        foreach (var file in scannedFiles)
        {
            var relativePath = _RepositoryPath(repositoryRoot, file);
            if (relativePath == "src/Cockpit.Core/Configuration/CockpitBrand.cs" || AllowedFiles.Contains(relativePath))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            foreach (var literal in ProtectedLiterals)
            {
                if (text.Contains(literal, StringComparison.Ordinal))
                {
                    found.Add($"{relativePath}: repeats \"{literal}\" instead of resolving CockpitBrand");
                }
            }
        }

        Assert.Empty(found);
    }

    private static IEnumerable<string> _ScannedFiles(string repositoryRoot) =>
        Directory.EnumerateFiles(Path.Combine(repositoryRoot, "src", "Cockpit.App"), "*.*", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(repositoryRoot, "src", "Cockpit.Core"), "*.*", SearchOption.AllDirectories))
            .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static string _RepositoryPath(string repositoryRoot, string file) =>
        Path.GetRelativePath(repositoryRoot, file).Replace(Path.DirectorySeparatorChar, '/');
}
