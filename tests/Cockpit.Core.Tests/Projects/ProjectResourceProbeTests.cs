using System.Diagnostics;
using Cockpit.Core.Projects;

namespace Cockpit.Core.Tests.Projects;

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

        Assert.Empty(result);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(500), "skipping a UNC path must not touch the network at all");
    }

}
