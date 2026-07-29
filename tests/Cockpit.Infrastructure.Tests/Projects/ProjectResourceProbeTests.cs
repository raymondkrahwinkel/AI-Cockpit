using System.Diagnostics;
using Cockpit.Core.Projects;
using Cockpit.Infrastructure.Projects;
using FluentAssertions;

namespace Cockpit.Infrastructure.Tests.Projects;

/// <summary>
/// The I/O <see cref="Cockpit.Core.Sessions.SessionStartDefaults.Resolve"/> deliberately never does itself
/// (AC-484): checking whether a resource's reference names something that actually exists. Scope is narrow on
/// purpose — see <see cref="ProjectResourceProbe"/>'s own remarks — so most of these tests are about what the probe
/// correctly says nothing about, not just what it flags.
/// </summary>
public class ProjectResourceProbeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cockpit-resource-probe-tests", Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void AnAbsolutePathThatDoesNotExist_IsReportedUnresolved()
    {
        var missing = Path.Combine(_root, "does-not-exist");
        var resources = new[] { new ProjectResource(missing, ProjectResourceRole.Memory) };

        ProjectResourceProbe.FindUnresolved(resources).Should().Contain(missing);
    }

    [Fact]
    public void AnAbsolutePathThatExistsAsAFile_IsNotReportedUnresolved()
    {
        Directory.CreateDirectory(_root);
        var file = Path.Combine(_root, "notes.md");
        File.WriteAllText(file, "hello");
        var resources = new[] { new ProjectResource(file, ProjectResourceRole.Memory) };

        ProjectResourceProbe.FindUnresolved(resources).Should().BeEmpty();
    }

    [Fact]
    public void AnAbsolutePathThatExistsAsADirectory_IsNotReportedUnresolved()
    {
        Directory.CreateDirectory(_root);
        var resources = new[] { new ProjectResource(_root, ProjectResourceRole.Memory) };

        ProjectResourceProbe.FindUnresolved(resources).Should().BeEmpty();
    }

    /// <summary>
    /// AC-484's explicit boundary: a <c>&lt;scheme&gt;:&lt;value&gt;</c> reference is the registering plugin's to
    /// judge, never this probe's — even though "depot" is not a real path and obviously does not exist on disk.
    /// </summary>
    [Fact]
    public void ASchemeReference_IsNeverReportedUnresolved()
    {
        var resources = new[] { new ProjectResource("depot:cockpit", ProjectResourceRole.Memory) };

        ProjectResourceProbe.FindUnresolved(resources).Should().BeEmpty();
    }

    /// <summary>
    /// AC-484's other explicit boundary: a relative path's portability is AC-485's question, so this probe says
    /// nothing about one at all — even one that plainly does not exist relative to the current directory.
    /// </summary>
    [Fact]
    public void ARelativePath_IsNeverReportedUnresolved()
    {
        var resources = new[] { new ProjectResource("notes/does-not-exist-either", ProjectResourceRole.Memory) };

        ProjectResourceProbe.FindUnresolved(resources).Should().BeEmpty();
    }

    [Fact]
    public void ABlankReference_IsNeverReportedUnresolved()
    {
        var resources = new[] { new ProjectResource("   ", ProjectResourceRole.Reference) };

        ProjectResourceProbe.FindUnresolved(resources).Should().BeEmpty();
    }

    /// <summary>
    /// AC-484 review (MUST-FIX 4): a UNC path is fully qualified, so before this fix it reached the existence
    /// check — and an unreachable host turned that check into a network round trip measured at 1282 ms,
    /// synchronous, on whichever thread called this (both current call sites are UI threads). Skipped before any
    /// I/O runs, the same way a scheme reference or a relative path already were.
    /// </summary>
    [Fact]
    public void AUncPath_IsNeverReportedUnresolvedAndNeverChecked()
    {
        var resources = new[] { new ProjectResource(@"\\unreachable-host\share\notes.md", ProjectResourceRole.Memory) };

        var stopwatch = Stopwatch.StartNew();
        var result = ProjectResourceProbe.FindUnresolved(resources);
        stopwatch.Stop();

        result.Should().BeEmpty("this probe cannot afford to judge a UNC path cheaply, so it says nothing about one");
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(500), "skipping a UNC path must not touch the network at all");
    }

    /// <summary>
    /// AC-484 review (MUST-FIX 4, part 3): a row that will never reach a starting session's prompt
    /// (<see cref="ProjectResource.ReachesSessions"/> false) gains nothing from being checked, so it must not cost
    /// any of the shared time budget or any I/O at all — proven here with a path that would otherwise be reported
    /// missing.
    /// </summary>
    [Fact]
    public void ARowThatDoesNotReachSessions_IsNeverCheckedFromDisk()
    {
        var missing = Path.Combine(_root, "does-not-exist");
        var resources = new[] { new ProjectResource(missing, ProjectResourceRole.Memory) { ReachesSessions = false } };

        ProjectResourceProbe.FindUnresolved(resources).Should().BeEmpty();
    }

    /// <summary>
    /// AC-484 review (MUST-FIX 4, part 2): the whole call carries a hard time budget rather than a per-row one — a
    /// caller waiting on a UI thread cares about the total wait. A row whose existence check does not answer within
    /// that budget must not be reported unresolved (an unanswered check is not evidence of anything), and the call
    /// itself must not block for as long as the slow check takes: the check runs on its own thread and this method
    /// only waits up to its share of the budget for it, exactly the guard that keeps an unreachable network path
    /// from freezing the caller even when it is not literally a UNC string — a mapped drive letter over the same
    /// slow link is the case this catches that the UNC skip alone does not.
    /// </summary>
    [Fact]
    public void ARowSlowerThanTheBudget_IsNotReportedEitherWayAndDoesNotBlock()
    {
        var resources = new[] { new ProjectResource(Path.Combine(_root, "slow"), ProjectResourceRole.Memory) };

        var stopwatch = Stopwatch.StartNew();
        var result = ProjectResourceProbe.FindUnresolved(
            resources,
            timeBudget: TimeSpan.FromMilliseconds(50),
            pathExists: _ =>
            {
                Thread.Sleep(2000);
                return false;
            });
        stopwatch.Stop();

        result.Should().BeEmpty("a row that did not answer within the time budget must not be reported broken");
        stopwatch.Elapsed.Should().BeLessThan(
            TimeSpan.FromSeconds(1), "the call itself must return once its budget is spent, not wait for the slow check to finish");
    }

    /// <summary>
    /// AC-484 confirming round (FIX 4): the class doc used to claim a reference this runtime cannot even parse as a
    /// path is "left out of the result rather than reported broken". On .NET 10, <see cref="File.Exists(string)"/>
    /// returns false instead of throwing for a path with invalid characters, so this never reaches the method's own
    /// <c>catch</c> block at all — it flows through as an ordinary "does not exist" and ends up reported, not left
    /// out.
    /// </summary>
    [Fact]
    public void AnAbsolutePathWithInvalidCharacters_IsReportedUnresolvedRatherThanSilentlySkipped()
    {
        // Rooted for the platform the test is running on, not for the one it was written on. The probe only judges a
        // fully-qualified path, and what counts as fully qualified is itself platform-specific: "C:\..." is rooted on
        // Windows and a plain relative name on Linux, where it would be skipped and this assertion would fail for a
        // reason that has nothing to do with awkward characters. That is not hypothetical — it is how this test first
        // reached CI: green on Windows, red on the Linux runner.
        var badPath = OperatingSystem.IsWindows() ? @"C:\bad<>|?*\0name.md" : "/bad<>|?*\0name.md";
        var resources = new[] { new ProjectResource(badPath, ProjectResourceRole.Memory) };

        ProjectResourceProbe.FindUnresolved(resources).Should().Contain(
            badPath, ".NET 10's File.Exists returns false rather than throwing for invalid characters, so this is reported broken, not left out");
    }

    /// <summary>
    /// AC-484 confirming round (FIX 4), the second measured case: an absurdly long path (32,000 characters) behaves
    /// the same way — never throws, and is reported unresolved rather than silently skipped.
    /// </summary>
    [Fact]
    public void AnAbsurdlyLongPath_IsReportedUnresolvedRatherThanSilentlySkipped()
    {
        // Rooted per platform for the same reason as the case above.
        var longPath = (OperatingSystem.IsWindows() ? @"C:\" : "/") + new string('a', 32_000);
        var resources = new[] { new ProjectResource(longPath, ProjectResourceRole.Memory) };

        var act = () => ProjectResourceProbe.FindUnresolved(resources);

        act.Should().NotThrow("the probe must never throw regardless of how the underlying File.Exists behaves for an absurd path");
        ProjectResourceProbe.FindUnresolved(resources).Should().Contain(longPath);
    }
}
