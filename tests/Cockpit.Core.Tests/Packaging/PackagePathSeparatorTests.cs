using System.Text.RegularExpressions;
using Cockpit.TestSupport;

namespace Cockpit.Core.Tests.Packaging;

/// <summary>
/// AC-1045: a backslash in a <c>PackagePath</c> attribute is read as part of the file name on Linux instead of as a
/// path separator, so the packed file lands with a literal backslash in its name instead of in the folder NuGet
/// meant — NU5129 only surfaces this on a Linux pack, so a cross-platform source scan catches it without one.
/// </summary>
public partial class PackagePathSeparatorTests
{
    [Fact]
    public void NoCsproj_UsesBackslashInPackagePath()
    {
        var repositoryRoot = RepositoryPaths.Root;
        var csprojFiles = Directory.EnumerateFiles(repositoryRoot, "*.csproj", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();

        Assert.True(csprojFiles.Count > 10,
            "the repo has well over a dozen .csproj files — finding almost none means the walk broke, not that the rule holds");

        var offenders = new List<string>();
        foreach (var file in csprojFiles)
        {
            if (PackagePathBackslashRegex().IsMatch(File.ReadAllText(file)))
            {
                offenders.Add(Path.GetRelativePath(repositoryRoot, file).Replace(Path.DirectorySeparatorChar, '/'));
            }
        }

        Assert.True(offenders.Count == 0,
            $"PackagePath with a backslash lands as a literal filename on Linux (AC-1045): {string.Join(", ", offenders)}");
    }

    [GeneratedRegex("PackagePath\\s*=\\s*\"[^\"]*\\\\[^\"]*\"")]
    private static partial Regex PackagePathBackslashRegex();
}
