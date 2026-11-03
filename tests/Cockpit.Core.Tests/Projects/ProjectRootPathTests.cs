using Cockpit.Core.Projects;

namespace Cockpit.Core.Tests.Projects;

public class ProjectRootPathTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "ac-1385-path-tests", "repo");

    [Theory]
    [InlineData("", false, false)]
    [InlineData("../x", false, false)]
    [InlineData("/etc/passwd", false, false)]
    [InlineData(@"C:\Windows\x", false, false)]
    [InlineData("C:x", false, false)]
    [InlineData(@"\\server\share\x", false, false)]
    [InlineData(@"\\?\C:\x", false, false)]
    [InlineData("a.txt:ads", false, true)]
    [InlineData("src/a.cs", true, true)]
    [InlineData("src/../README.md", true, true)]
    [InlineData("SRC/A.CS", true, true)]
    // A sibling sharing the root's name as a prefix: the containment check needs a separator boundary.
    [InlineData("../repo2/x", false, false)]
    // The same two with Windows' own separator: on Unix a backslash is a file-name character, so both stay inside.
    [InlineData(@"SRC\A.CS", true, true)]
    [InlineData(@"..\repo2\x", false, true)]
    public void TryResolve_RejectsUnsafePathsAndAllowsContainedPaths(
        string relativePath, bool allowedOnWindows, bool allowedOnUnix)
    {
        var accepted = ProjectRootPath.TryResolve(Root, relativePath, out var fullPath, out var refusal);
        var expected = OperatingSystem.IsWindows() ? allowedOnWindows : allowedOnUnix;

        Assert.Equal(expected, accepted);
        Assert.Equal(expected, refusal is null);
        Assert.Equal(expected, fullPath.Length > 0);
    }

    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void TryResolve_ChecksLinkTargets(bool targetInside)
    {
        var sandbox = Path.Combine(Path.GetTempPath(), "ac-1385-links-" + Guid.NewGuid().ToString("N"));
        var root = Directory.CreateDirectory(Path.Combine(sandbox, "root")).FullName;
        var inside = Directory.CreateDirectory(Path.Combine(root, "inside")).FullName;
        var outside = Directory.CreateDirectory(Path.Combine(sandbox, "outside")).FullName;
        var link = Path.Combine(root, "link");
        var rootAlias = Path.Combine(sandbox, "root-alias");

        try
        {
            Skip.IfNot(TryCreateDirectoryLink(link, targetInside ? inside : outside),
                "This machine cannot create a junction or directory symlink.");
            Skip.IfNot(TryCreateDirectoryLink(rootAlias, root),
                "This machine cannot create a junction or directory symlink for the project root.");

            var accepted = ProjectRootPath.TryResolve(root, Path.Combine("link", "file.txt"), out _, out var refusal);
            var aliasAccepted = ProjectRootPath.TryResolve(rootAlias, Path.Combine("inside", "file.txt"), out var resolved, out var aliasRefusal);

            Assert.Equal(targetInside, accepted);
            Assert.Equal(targetInside, refusal is null);
            Assert.True(aliasAccepted, aliasRefusal);
            Assert.Equal(Path.Combine(inside, "file.txt"), resolved);
        }
        finally
        {
            RemoveLinkAndSandbox(link, rootAlias, sandbox);
        }
    }

    private static bool TryCreateDirectoryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    private static void RemoveLinkAndSandbox(string link, string rootAlias, string sandbox)
    {
        try
        {
            Directory.Delete(link);
        }
        catch (DirectoryNotFoundException)
        {
        }

        try
        {
            Directory.Delete(rootAlias);
        }
        catch (DirectoryNotFoundException)
        {
        }

        Directory.Delete(sandbox, recursive: true);
    }
}
