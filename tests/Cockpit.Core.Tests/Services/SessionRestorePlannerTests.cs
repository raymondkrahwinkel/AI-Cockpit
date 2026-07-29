using Cockpit.App.Services;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Core.Workspaces;
using NSubstitute;

namespace Cockpit.Core.Tests.Services;

/// <summary>
/// AC-410 step 4: <see cref="SessionRestorePlanner"/> answers "what can this saved pane be restored with" without
/// starting anything, modelled on <see cref="ProjectQuickStart"/>. A profile the config can no longer resolve
/// degrades the plan rather than crashing — the same posture <c>ProjectQuickStart.ComposeAsync</c> takes for a
/// missing profile.
/// </summary>
public class SessionRestorePlannerTests
{
    private static readonly SessionProfile WorkProfile = new("work", new ClaudeConfig(@"C:\fake\.claude"));

    private static WorkspacePane Pane(string? profileId = "work") =>
        new("pane-1", PaneKind.AiSession) { ProfileId = profileId };

    private static SessionRestorePlanner Build(params SessionProfile[] profiles)
    {
        var store = Substitute.For<ISessionProfileStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(profiles);
        return new SessionRestorePlanner(store);
    }

    [Fact]
    public async Task ComposeAsync_TheProfileNoLongerExists_DegradesRatherThanThrowing()
    {
        var planner = Build(); // no profiles configured at all

        var plan = await planner.ComposeAsync(Pane("gone"), state: null);

        Assert.Equal(SessionRestoreAvailability.ProfileGone, plan.Availability);
        Assert.Null(plan.Profile);
        Assert.Contains("gone", plan.Explanation);
    }

    [Fact]
    public async Task ComposeAsync_ProfileMatchingIsCaseInsensitive()
    {
        var planner = Build(WorkProfile);

        var plan = await planner.ComposeAsync(Pane("WORK"), state: null);

        Assert.NotNull(plan.Profile);
        Assert.Equal("work", plan.Profile!.Label);
    }

    [Fact]
    public async Task ComposeAsync_NoSavedStateForThePane_YieldsUnknown()
    {
        var planner = Build(WorkProfile);

        var plan = await planner.ComposeAsync(Pane(), state: null);

        Assert.Equal(SessionRestoreAvailability.Unknown, plan.Availability);
        Assert.NotNull(plan.Profile);
    }

    [Fact]
    public async Task ComposeAsync_AKnownConversationId_YieldsKnown()
    {
        var planner = Build(WorkProfile);
        var state = new SessionStateRecord("pane-1", "work", "ClaudeCli", "conv-1", SessionConversationIdState.Known, "/repo", null, null, null, DateTimeOffset.UtcNow);

        var plan = await planner.ComposeAsync(Pane(), state);

        Assert.Equal(SessionRestoreAvailability.Known, plan.Availability);
    }

    [Fact]
    public async Task ComposeAsync_AnUnsupportedProvider_YieldsUnsupported()
    {
        var planner = Build(WorkProfile);
        var state = new SessionStateRecord("pane-1", "work", "Ollama", null, SessionConversationIdState.Unsupported, "/repo", null, null, null, DateTimeOffset.UtcNow);

        var plan = await planner.ComposeAsync(Pane(), state);

        Assert.Equal(SessionRestoreAvailability.Unsupported, plan.Availability);
    }

    [Fact]
    public async Task ComposeAsync_AWorktreePathThatNoLongerExistsOnDisk_YieldsWorktreeGone()
    {
        var planner = Build(WorkProfile);
        var missingPath = Path.Combine(Path.GetTempPath(), $"cockpit-gone-{Guid.NewGuid():n}");
        var state = new SessionStateRecord("pane-1", "work", "ClaudeCli", "conv-1", SessionConversationIdState.Known, missingPath, missingPath, "cockpit/x", null, DateTimeOffset.UtcNow);

        var plan = await planner.ComposeAsync(Pane(), state);

        Assert.Equal(SessionRestoreAvailability.WorktreeGone, plan.Availability);
    }

    [Fact]
    public async Task ComposeAsync_AWorktreePathThatStillExists_DoesNotReportItGone()
    {
        var planner = Build(WorkProfile);
        var state = new SessionStateRecord("pane-1", "work", "ClaudeCli", "conv-1", SessionConversationIdState.Known, Path.GetTempPath(), Path.GetTempPath(), "cockpit/x", null, DateTimeOffset.UtcNow);

        var plan = await planner.ComposeAsync(Pane(), state);

        Assert.Equal(SessionRestoreAvailability.Known, plan.Availability);
    }

    [Fact]
    public async Task ComposeAsync_APaneWithNoProfileId_YieldsUnknownRatherThanCrashing()
    {
        var planner = Build(WorkProfile);

        var plan = await planner.ComposeAsync(Pane(profileId: null), state: null);

        Assert.Equal(SessionRestoreAvailability.Unknown, plan.Availability);
        Assert.Null(plan.Profile);
    }
}
