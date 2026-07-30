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

    /// <summary>
    /// AC-513 criterion 4: a pane that genuinely never had a conversation id — no <c>SessionStateRecord</c> was
    /// ever written for it at all (a crash before the session got far enough to report anything) — must not just
    /// carry <see cref="SessionRestoreAvailability.Unknown"/>, the banner has to be able to say why in words. This
    /// was already true before AC-513 (<see cref="SessionRestorePlanner.ComposeAsync"/>'s no-state branch has
    /// always set an explanation); pinned here as a regression test rather than left implicit.
    /// </summary>
    [Fact]
    public async Task ComposeAsync_NoSavedStateForThePane_NamesTheReasonInWords()
    {
        var planner = Build(WorkProfile);

        var plan = await planner.ComposeAsync(Pane(), state: null);

        Assert.False(string.IsNullOrWhiteSpace(plan.Explanation));
        Assert.Contains("conversation", plan.Explanation, StringComparison.Ordinal);
    }

    /// <summary>
    /// The other shape of "never had an id": a <c>SessionStateRecord</c> exists (the session did start, and a
    /// pane record was written) but its <c>ConversationState</c> never moved past <see cref="SessionConversationIdState.Unknown"/> —
    /// the session crashed before its provider ever reported one. Distinct from the no-record case above; both
    /// must still name the reason.
    /// </summary>
    [Fact]
    public async Task ComposeAsync_ARecordWhoseConversationWasNeverReported_YieldsUnknownWithAReason()
    {
        var planner = Build(WorkProfile);
        var state = new SessionStateRecord("pane-1", "work", "ClaudeCli", null, SessionConversationIdState.Unknown, "/repo", null, null, null, DateTimeOffset.UtcNow);

        var plan = await planner.ComposeAsync(Pane(), state);

        Assert.Equal(SessionRestoreAvailability.Unknown, plan.Availability);
        Assert.False(string.IsNullOrWhiteSpace(plan.Explanation));
        Assert.Contains("conversation", plan.Explanation, StringComparison.Ordinal);
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
