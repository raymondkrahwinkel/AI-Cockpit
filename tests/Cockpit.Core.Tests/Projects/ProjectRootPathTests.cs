using Cockpit.Core.Projects;
using Cockpit.Core.Sessions;
using System.Diagnostics;

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
        Directory.CreateDirectory(Path.Combine(inside, "nested"));
        Directory.CreateDirectory(Path.Combine(outside, "nested"));
        var link = Path.Combine(root, "link");
        var rootAlias = Path.Combine(sandbox, "root-alias");
        var indirect = Path.Combine(root, "indirect");

        try
        {
            Skip.IfNot(TryCreateDirectoryLink(link, targetInside ? inside : outside),
                "This machine cannot create a junction or directory symlink.");
            Skip.IfNot(TryCreateDirectoryLink(rootAlias, root),
                "This machine cannot create a junction or directory symlink for the project root.");
            Skip.IfNot(TryCreateDirectoryLink(indirect, Path.Combine(link, "nested")),
                "This machine cannot create an indirect junction or directory symlink.");

            var accepted = ProjectRootPath.TryResolve(root, Path.Combine("link", "file.txt"), out _, out var refusal);
            var aliasAccepted = ProjectRootPath.TryResolve(rootAlias, Path.Combine("inside", "file.txt"), out var resolved, out var aliasRefusal);

            Assert.Equal(targetInside, accepted);
            Assert.Equal(targetInside, refusal is null);
            Assert.True(aliasAccepted, aliasRefusal);
            Assert.Equal(Path.Combine(inside, "file.txt"), resolved);

            var attachment = Path.Combine(root, "file.txt");
            File.WriteAllText(attachment, "attachment");
            using var opened = File.OpenRead(attachment);
            Assert.True(Cockpit.Infrastructure.Assistant.ReplyAttachmentPath.TryResolve(
                Cockpit.Infrastructure.Assistant.ReplyAttachmentPath.OpenedPath(opened), [root], out _, out var attachmentRefusal), attachmentRefusal);
            Assert.False(Cockpit.Infrastructure.Assistant.ReplyAttachmentPath.TryResolve(
                Path.Combine(root, "cockpit.json"), [root], out _, out var configRefusal));
            Assert.NotNull(configRefusal);
            Assert.Equal(targetInside, Cockpit.Infrastructure.Assistant.ReplyAttachmentPath.TryResolve(
                Path.Combine(link, "file.txt"), [root], out _, out var linkRefusal));
            Assert.Equal(targetInside, linkRefusal is null);

            Assert.True(PathBoundary.Evaluate(Path.Combine(inside, "file.txt"), [root]).IsInside);
            Assert.False(PathBoundary.Evaluate(Path.Combine(outside, "file.txt"), [root]).IsInside);
            Assert.False(PathBoundary.Evaluate(Path.Combine(root, "..", "outside", "file.txt"), [root]).IsInside);
            Assert.False(PathBoundary.Evaluate(root + "-other/file.txt", [root]).IsInside);
            Assert.Equal(targetInside, PathBoundary.Evaluate(Path.Combine(link, "file.txt"), [root]).IsInside);
            Assert.Equal(targetInside, PathBoundary.Evaluate(Path.Combine(indirect, "file.txt"), [root]).IsInside);
            Assert.Equal(OperatingSystem.IsWindows(), PathBoundary.Evaluate(Path.Combine(inside, "file.txt").ToUpperInvariant(), [root]).IsInside);
            Assert.True(PathBoundary.Evaluate(Path.Combine(outside, "scratch.txt"), [root, outside]).IsInside);
        }
        finally
        {
            RemoveLinkAndSandbox(link, rootAlias, indirect, sandbox);
        }
    }

    private static bool TryCreateDirectoryLink(string link, string target)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                start.ArgumentList.Add("/c");
                start.ArgumentList.Add("mklink");
                start.ArgumentList.Add("/J");
                start.ArgumentList.Add(link);
                start.ArgumentList.Add(target);
                using var process = Process.Start(start);
                process?.WaitForExit();
                return process?.ExitCode == 0;
            }
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    private static void RemoveLinkAndSandbox(string link, string rootAlias, string indirect, string sandbox)
    {
        try
        {
            Directory.Delete(indirect);
        }
        catch (DirectoryNotFoundException)
        {
        }

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
