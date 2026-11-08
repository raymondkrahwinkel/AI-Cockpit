using Cockpit.Infrastructure.Agents;

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

    /// <summary>
    /// "Has not answered" and "answered no" are different states, and the difference is the whole point of the
    /// override: a session that opted out must not be opted back in by the operator changing a setting later.
    /// </summary>
    [Fact]
    public void APaneThatAnsweredForItself_KeepsItsAnswerWhateverTheSettingDoes()
    {
        {
            var scenario = new AgentWakeConsentDefaultTests();
            scenario._AssertAPaneThatAnsweredForItself_KeepsItsAnswerWhateverTheSettingDoes(true);
        }
        {
            var scenario = new AgentWakeConsentDefaultTests();
            scenario._AssertAPaneThatAnsweredForItself_KeepsItsAnswerWhateverTheSettingDoes(false);
        }
    }

    private void _AssertAPaneThatAnsweredForItself_KeepsItsAnswerWhateverTheSettingDoes(bool ownAnswer)
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

}
