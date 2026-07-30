using Cockpit.Core.Abstractions.Agents;
using Cockpit.Infrastructure.Agents;

namespace Cockpit.Infrastructure.Tests.Agents;

/// <summary>
/// AC-439's operator-facing signal: which panes hold a claim on the same physical resource as a pane on a
/// <em>different</em> workspace. Built against fakes for both dependencies rather than the concrete
/// <see cref="AgentResourceClaims"/>/App-layer directory, so a test here is purely about the grouping logic — same
/// resource, different workspace, unknown pane excluded — and not about how the store or the directory are wired.
/// </summary>
public sealed class ClaimCollisionMonitorTests
{
    private sealed class FakeClaimsAudit(params AgentResourceClaim[] claims) : IAgentResourceClaimsAudit
    {
        public IReadOnlyList<AgentResourceClaim> ListAll() => claims;
    }

    private sealed class FakePaneWorkspaceDirectory(IReadOnlyDictionary<string, string> byPane) : IPaneWorkspaceDirectory
    {
        public IReadOnlyDictionary<string, string> WorkspaceIdsByPane() => byPane;
    }

    private static IReadOnlyDictionary<string, string> _Desks(params (string PaneId, string WorkspaceId)[] entries) =>
        entries.ToDictionary(entry => entry.PaneId, entry => entry.WorkspaceId, StringComparer.Ordinal);

    [Fact]
    public void PanesInCollision_SameResourceOnTwoWorkspaces_ReportsBothPanes()
    {
        var claims = new FakeClaimsAudit(
            new AgentResourceClaim("feature/AC-439", "pane-x", DateTimeOffset.UtcNow),
            new AgentResourceClaim("feature/AC-439", "pane-y", DateTimeOffset.UtcNow));
        var directory = new FakePaneWorkspaceDirectory(_Desks(("pane-x", "ws-1"), ("pane-y", "ws-2")));
        var monitor = new ClaimCollisionMonitor(claims, directory);

        var colliding = monitor.PanesInCollision();

        Assert.Equal(2, colliding.Count);
        Assert.Contains("pane-x", colliding);
        Assert.Contains("pane-y", colliding);
    }

    /// <summary>
    /// Guards the "more than one distinct workspace" condition specifically: two owners on the <em>same</em> desk are
    /// the ordinary within-desk case — either the same claim or one <see cref="AgentResourceClaims.Claim"/> would
    /// already have refused — and must never light the operator's chip. A monitor that grouped by resource alone,
    /// without checking the owners' workspaces differ, would pass every other test here and still fail this one.
    /// </summary>
    [Fact]
    public void PanesInCollision_SameResourceOnOneWorkspace_ReportsNothing()
    {
        var claims = new FakeClaimsAudit(
            new AgentResourceClaim("feature/AC-439", "pane-x", DateTimeOffset.UtcNow),
            new AgentResourceClaim("feature/AC-439", "pane-y", DateTimeOffset.UtcNow));
        var directory = new FakePaneWorkspaceDirectory(_Desks(("pane-x", "ws-1"), ("pane-y", "ws-1")));
        var monitor = new ClaimCollisionMonitor(claims, directory);

        Assert.Empty(monitor.PanesInCollision());
    }

    [Fact]
    public void PanesInCollision_DifferentResources_ReportsNothing()
    {
        var claims = new FakeClaimsAudit(
            new AgentResourceClaim("feature/AC-439", "pane-x", DateTimeOffset.UtcNow),
            new AgentResourceClaim("feature/AC-440", "pane-y", DateTimeOffset.UtcNow));
        var directory = new FakePaneWorkspaceDirectory(_Desks(("pane-x", "ws-1"), ("pane-y", "ws-2")));
        var monitor = new ClaimCollisionMonitor(claims, directory);

        Assert.Empty(monitor.PanesInCollision());
    }

    /// <summary>
    /// A claim whose owner has already closed — racing with its own <c>Forget</c> — must not be read as a second
    /// desk. Counting an unknown pane as its own workspace would report a collision that is not real.
    /// </summary>
    [Fact]
    public void PanesInCollision_OwnerNotInDirectory_IsExcludedRatherThanCountedAsASecondWorkspace()
    {
        var claims = new FakeClaimsAudit(
            new AgentResourceClaim("feature/AC-439", "pane-x", DateTimeOffset.UtcNow),
            new AgentResourceClaim("feature/AC-439", "pane-gone", DateTimeOffset.UtcNow));
        var directory = new FakePaneWorkspaceDirectory(_Desks(("pane-x", "ws-1")));
        var monitor = new ClaimCollisionMonitor(claims, directory);

        Assert.Empty(monitor.PanesInCollision());
    }

    [Fact]
    public void PanesInCollision_ThreeWorkspacesOnOneResource_ReportsAllThree()
    {
        var claims = new FakeClaimsAudit(
            new AgentResourceClaim("feature/AC-439", "pane-x", DateTimeOffset.UtcNow),
            new AgentResourceClaim("feature/AC-439", "pane-y", DateTimeOffset.UtcNow),
            new AgentResourceClaim("feature/AC-439", "pane-z", DateTimeOffset.UtcNow));
        var directory = new FakePaneWorkspaceDirectory(_Desks(("pane-x", "ws-1"), ("pane-y", "ws-2"), ("pane-z", "ws-3")));
        var monitor = new ClaimCollisionMonitor(claims, directory);

        Assert.Equal(3, monitor.PanesInCollision().Count);
    }

    [Fact]
    public void PanesInCollision_NoClaims_ReportsNothing()
    {
        var monitor = new ClaimCollisionMonitor(new FakeClaimsAudit(), new FakePaneWorkspaceDirectory(_Desks()));

        Assert.Empty(monitor.PanesInCollision());
    }

    /// <summary>The physical-identity layer, exercised end to end: a trailing separator is a different string but the
    /// same directory, and the two desks holding it must still collide.</summary>
    [Fact]
    public void PanesInCollision_PathsThatCanonicalizeToTheSamePhysicalDirectory_CollideAcrossWorkspaces()
    {
        var real = Directory.CreateTempSubdirectory("ac439-");
        try
        {
            var spelledWithTrailingSeparator = real.FullName + Path.DirectorySeparatorChar;
            var claims = new FakeClaimsAudit(
                new AgentResourceClaim(real.FullName, "pane-x", DateTimeOffset.UtcNow),
                new AgentResourceClaim(spelledWithTrailingSeparator, "pane-y", DateTimeOffset.UtcNow));
            var directory = new FakePaneWorkspaceDirectory(_Desks(("pane-x", "ws-1"), ("pane-y", "ws-2")));
            var monitor = new ClaimCollisionMonitor(claims, directory);

            Assert.Equal(2, monitor.PanesInCollision().Count);
        }
        finally
        {
            real.Delete();
        }
    }

    /// <summary>
    /// The case the ticket names explicitly: a worktree reached through a symlink is the same physical thing as the
    /// real path. Skipped on Windows, where creating a symlink from a test process needs a privilege this suite
    /// cannot assume — the documented gap in <see cref="PhysicalResourceIdentity"/> covers this platform difference.
    /// </summary>
    [Fact]
    public void PanesInCollision_ResourceReachedThroughASymlink_CollidesWithTheRealPath()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Directory.CreateTempSubdirectory("ac439-");
        try
        {
            var real = Directory.CreateDirectory(Path.Combine(root.FullName, "real-worktree"));
            var symlinkPath = Path.Combine(root.FullName, "linked-worktree");
            Directory.CreateSymbolicLink(symlinkPath, real.FullName);

            var claims = new FakeClaimsAudit(
                new AgentResourceClaim(real.FullName, "pane-x", DateTimeOffset.UtcNow),
                new AgentResourceClaim(symlinkPath, "pane-y", DateTimeOffset.UtcNow));
            var directory = new FakePaneWorkspaceDirectory(_Desks(("pane-x", "ws-1"), ("pane-y", "ws-2")));
            var monitor = new ClaimCollisionMonitor(claims, directory);

            Assert.Equal(2, monitor.PanesInCollision().Count);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    /// <summary>
    /// Acceptance criterion 3: the chip is never dismissed, it disappears on its own once either side releases. Run
    /// against the real <see cref="AgentResourceClaims"/> store rather than the fakes above, so this proves the
    /// monitor's "no cache, recomputed from whatever <c>ListAll</c> says right now" design actually behaves that way
    /// end to end — release one of the two colliding claims and the very next call reports nobody colliding.
    /// </summary>
    [Fact]
    public void PanesInCollision_AfterEitherClaimIsReleased_NoLongerReportsTheCollision()
    {
        var claims = new AgentResourceClaims();
        var directory = new FakePaneWorkspaceDirectory(_Desks(("pane-x", "ws-1"), ("pane-y", "ws-2")));
        var monitor = new ClaimCollisionMonitor(claims, directory);

        claims.Claim("pane-x", "feature/AC-439", new HashSet<string>(StringComparer.Ordinal) { "pane-x" });
        claims.Claim("pane-y", "feature/AC-439", new HashSet<string>(StringComparer.Ordinal) { "pane-y" });
        Assert.Equal(2, monitor.PanesInCollision().Count);

        claims.Release("pane-y", "feature/AC-439", new HashSet<string>(StringComparer.Ordinal) { "pane-y" });

        Assert.Empty(monitor.PanesInCollision());
    }

    /// <summary>Two genuinely different physical paths must never collide — the negative case for the test above.</summary>
    [Fact]
    public void PanesInCollision_TwoDifferentPhysicalDirectories_DoNotCollide()
    {
        var root = Directory.CreateTempSubdirectory("ac439-");
        try
        {
            var first = Directory.CreateDirectory(Path.Combine(root.FullName, "worktree-a"));
            var second = Directory.CreateDirectory(Path.Combine(root.FullName, "worktree-b"));

            var claims = new FakeClaimsAudit(
                new AgentResourceClaim(first.FullName, "pane-x", DateTimeOffset.UtcNow),
                new AgentResourceClaim(second.FullName, "pane-y", DateTimeOffset.UtcNow));
            var directory = new FakePaneWorkspaceDirectory(_Desks(("pane-x", "ws-1"), ("pane-y", "ws-2")));
            var monitor = new ClaimCollisionMonitor(claims, directory);

            Assert.Empty(monitor.PanesInCollision());
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
