using Cockpit.Infrastructure.Agents;

namespace Cockpit.Infrastructure.Tests.Agents;

/// <summary>
/// The coordinator's own roster (AC-391), independent of the MCP tool that drives it: enrollment is keyed on pane
/// id alone, idempotent, and a forgotten pane goes back to reporting unenrolled. It is not partitioned by
/// workspace — an earlier revision was, and that stranded a pane's enrollment the moment its <em>resolved</em>
/// workspace drifted (the gateway's "first Sessions workspace" fallback can point elsewhere after the operator
/// closes a desk, with the pane itself never moving) — so there is nothing to prove about workspace isolation
/// here; that boundary belongs to <c>WorkspaceAgentGateway</c>, which is what decides which panes ever reach
/// <see cref="WorkspaceAgentCoordinator.IsEnrolled"/> for a given caller in the first place.
/// </summary>
public sealed class WorkspaceAgentCoordinatorTests
{
    /// <summary>
    /// AC-613's split, and the reason it exists: enrollment is the host saying it knows about a pane, contact is the
    /// pane reaching this server. A pane the host enrolled has no contact time until it calls in, and that null is
    /// what <c>list_agents</c> reports a gap on — the AC-156 signal that a single flag would have swallowed.
    /// </summary>
    [Fact]
    public void Enroll_WithoutContact_LeavesTheContactTimeUnset()
    {
        var coordinator = new WorkspaceAgentCoordinator();

        coordinator.Enroll("pane-1");

        Assert.True(coordinator.IsEnrolled("pane-1"));
        Assert.Null(coordinator.LastContactUtc("pane-1"));
    }

    [Fact]
    public void RecordContact_EnrollsAndStampsTheMoment()
    {
        var coordinator = new WorkspaceAgentCoordinator();
        var before = DateTimeOffset.UtcNow;

        coordinator.RecordContact("pane-1");

        Assert.True(coordinator.IsEnrolled("pane-1"));
        Assert.True(coordinator.LastContactUtc("pane-1") >= before);
    }

    /// <summary>
    /// The host enrolls repeatedly — once per snapshot, so on every tool call any pane on the desk makes. If that
    /// overwrote the entry it would erase the contact time and the wake consent of a pane that had already said both,
    /// which is the failure the roster's single-entry shape exists to make impossible.
    /// </summary>
    [Fact]
    public void Enroll_AfterContactAndConsent_ClearsNeither()
    {
        var coordinator = new WorkspaceAgentCoordinator();
        coordinator.RecordContact("pane-1");
        coordinator.SetWakeConsent("pane-1", true);
        var contacted = coordinator.LastContactUtc("pane-1");

        coordinator.Enroll("pane-1");

        Assert.Equal(contacted, coordinator.LastContactUtc("pane-1"));
        Assert.True(coordinator.HasWakeConsent("pane-1"));
    }

    /// <summary>Consent is set on panes that may have contacted, and must not reach into the other field either.</summary>
    [Fact]
    public void SetWakeConsent_LeavesTheContactTimeAlone()
    {
        var coordinator = new WorkspaceAgentCoordinator();
        coordinator.RecordContact("pane-1");
        var contacted = coordinator.LastContactUtc("pane-1");

        coordinator.SetWakeConsent("pane-1", true);
        coordinator.SetWakeConsent("pane-1", false);

        Assert.Equal(contacted, coordinator.LastContactUtc("pane-1"));
    }

    [Fact]
    public void Forget_TakesTheContactTimeWithIt()
    {
        var coordinator = new WorkspaceAgentCoordinator();
        coordinator.RecordContact("pane-1");

        coordinator.Forget("pane-1");

        Assert.Null(coordinator.LastContactUtc("pane-1"));
        Assert.False(coordinator.IsEnrolled("pane-1"));
    }

    [Fact]
    public void LastContactUtc_ForAPaneTheRosterNeverSaw_IsNull()
    {
        var coordinator = new WorkspaceAgentCoordinator();

        Assert.Null(coordinator.LastContactUtc("pane-1"));
    }

    [Fact]
    public void IsEnrolled_ForAPaneThatNeverCalledIn_ReportsFalse()
    {
        var coordinator = new WorkspaceAgentCoordinator();

        Assert.False(coordinator.IsEnrolled("pane-1"));
    }

    [Fact]
    public void Enroll_IsIdempotent_CallingTwiceStillReportsExactlyEnrolled()
    {
        var coordinator = new WorkspaceAgentCoordinator();

        coordinator.Enroll("pane-1");
        coordinator.Enroll("pane-1");

        // Not just "still true" (a List<string> full of duplicates would pass that too): a second pane that was
        // never enrolled must still read false, proving the first Enroll did not do something broader than record
        // this one pane exactly once.
        Assert.True(coordinator.IsEnrolled("pane-1"));
        Assert.False(coordinator.IsEnrolled("pane-2"));
    }

    [Fact]
    public void Forget_AnEnrolledPane_ReportsUnenrolledAfterwards()
    {
        var coordinator = new WorkspaceAgentCoordinator();
        coordinator.Enroll("pane-1");

        coordinator.Forget("pane-1");

        Assert.False(coordinator.IsEnrolled("pane-1"));
    }

    [Fact]
    public void Forget_APaneNeverEnrolled_IsANoOp()
    {
        var coordinator = new WorkspaceAgentCoordinator();

        coordinator.Forget("pane-1");

        Assert.False(coordinator.IsEnrolled("pane-1"));
    }

    [Fact]
    public void Forget_OnlyAffectsTheNamedPane()
    {
        var coordinator = new WorkspaceAgentCoordinator();
        coordinator.Enroll("pane-1");
        coordinator.Enroll("pane-2");

        coordinator.Forget("pane-1");

        Assert.False(coordinator.IsEnrolled("pane-1"));
        Assert.True(coordinator.IsEnrolled("pane-2"));
    }

    /// <summary>
    /// The class exists because MCP tool calls from several sessions' own request threads land concurrently
    /// (its own docstring leads with that); this is the one test that actually drives it that way, rather than
    /// only ever calling it from a single thread like every test above. Many panes enrolling, being checked, and
    /// being forgotten at once must never throw and must leave every pane that was enrolled-and-not-forgotten
    /// last reporting enrolled.
    /// </summary>
    [Fact]
    public async Task ConcurrentEnrollIsEnrolledAndForget_AcrossManyPanes_NeverThrowsAndEndsConsistent()
    {
        var coordinator = new WorkspaceAgentCoordinator();
        const int paneCount = 64;
        var paneIds = Enumerable.Range(0, paneCount).Select(i => $"pane-{i}").ToArray();

        // Half the panes are enrolled and left alone; the other half are enrolled, hammered with concurrent
        // IsEnrolled reads, and then forgotten — all three operations racing across every pane at once.
        var work = paneIds.Select((paneId, index) => Task.Run(() =>
        {
            coordinator.Enroll(paneId);
            for (var read = 0; read < 20; read++)
            {
                coordinator.IsEnrolled(paneId);
            }

            if (index % 2 == 0)
            {
                coordinator.Forget(paneId);
            }
        }));

        await Task.WhenAll(work);

        for (var i = 0; i < paneCount; i++)
        {
            var expectStillEnrolled = i % 2 != 0;
            Assert.Equal(expectStillEnrolled, coordinator.IsEnrolled($"pane-{i}"));
        }
    }
}
