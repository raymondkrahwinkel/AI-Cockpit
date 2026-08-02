using Cockpit.Core.SessionBehavior;
using Cockpit.Infrastructure.Agents;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Tests.Agents;

/// <summary>
/// Where the consent for a wake lives after AC-615: with the operator, on by default, and overridable by a session
/// for itself.
/// <para>
/// The reason for the move is worth restating, because the shape only makes sense with it. Opt-in per session was
/// the right instinct — a wake spends a turn belonging to somebody else's operator — and the wrong placement: an
/// agent will not spend its operator's money on its own say-so, and the operator never saw the choice because it was
/// an MCP call rather than a setting. Every pane in the field ran with <c>wakeOptIn: false</c>. The route was built
/// and never used.
/// </para>
/// </summary>
public sealed class AgentWakeConsentDefaultTests
{
    [Fact]
    public void APaneThatHasNotAnswered_FollowsTheOperatorsSetting()
    {
        var coordinator = new WorkspaceAgentCoordinator();
        coordinator.Enroll("pane-1");

        coordinator.SetDefaultWakeConsent(true);
        Assert.True(coordinator.HasWakeConsent("pane-1"));

        coordinator.SetDefaultWakeConsent(false);
        Assert.False(coordinator.HasWakeConsent("pane-1"));
    }

    /// <summary>
    /// "Has not answered" and "answered no" are different states, and the difference is the whole point of the
    /// override: a session that opted out must not be opted back in by the operator changing a setting later.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void APaneThatAnsweredForItself_KeepsItsAnswerWhateverTheSettingDoes(bool ownAnswer)
    {
        var coordinator = new WorkspaceAgentCoordinator();
        coordinator.SetWakeConsent("pane-1", ownAnswer);

        coordinator.SetDefaultWakeConsent(true);
        Assert.Equal(ownAnswer, coordinator.HasWakeConsent("pane-1"));

        coordinator.SetDefaultWakeConsent(false);
        Assert.Equal(ownAnswer, coordinator.HasWakeConsent("pane-1"));

        Assert.True(coordinator.HasOwnWakeConsent("pane-1"));
    }

    /// <summary>
    /// A pane that has ended is wakeable by nothing, whatever the setting says — a wake starts a turn, and there is
    /// no session to start one on. This is the one case where the default must not apply.
    /// </summary>
    [Fact]
    public void AForgottenPane_IsNotWakeableEvenWithTheSettingOn()
    {
        var coordinator = new WorkspaceAgentCoordinator();
        coordinator.SetDefaultWakeConsent(true);
        coordinator.Enroll("pane-1");
        Assert.True(coordinator.HasWakeConsent("pane-1"));

        coordinator.Forget("pane-1");

        Assert.False(coordinator.HasWakeConsent("pane-1"));
    }

    [Fact]
    public void TheShippedDefault_IsOn()
    {
        // Both halves, because they are set in different places and a mismatch would mean the coordinator disagreed
        // with the settings file about what an operator who has never touched the toggle agreed to.
        Assert.True(new SessionBehaviorSettings().WakeAgentsByDefault);

        var coordinator = new WorkspaceAgentCoordinator();
        coordinator.Enroll("pane-1");
        Assert.True(coordinator.HasWakeConsent("pane-1"));
    }

    /// <summary>
    /// A <c>cockpit.json</c> written before this setting existed has no such key. Deserialising that must read back
    /// as the default rather than as the operator having said no — otherwise "this setting did not exist yet" turns
    /// silently into "wake is off" for every install that predates it.
    /// </summary>
    [Fact]
    public void ASettingsFileFromBeforeThisSetting_ReadsBackAsOn()
    {
        var entry = new SessionBehaviorSettingsEntry();

        Assert.True(entry.WakeAgentsByDefault);
        Assert.True(entry.ToDomain().WakeAgentsByDefault);
    }

    [Fact]
    public void TheSettingSurvivesARoundTripThroughItsOnDiskShape()
    {
        var settings = new SessionBehaviorSettings { WakeAgentsByDefault = false };

        var round = SessionBehaviorSettingsEntry.FromDomain(settings).ToDomain();

        Assert.False(round.WakeAgentsByDefault);
    }
}
