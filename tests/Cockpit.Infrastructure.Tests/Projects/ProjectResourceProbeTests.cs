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
}
