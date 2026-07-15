using Cockpit.Core.Delegation;
using FluentAssertions;

namespace Cockpit.Core.Tests.Delegation;

/// <summary>
/// A session that can delegate is told so (#67). Tool descriptions explain what the orchestrator's tools do but
/// not when using them is worthwhile, so without this an agent that could hand bulk work to a local model simply
/// does it itself and the whole feature sits unused. The spawn-time injection of this prompt now lives in the
/// provider plugins (Claude's argument builder, Codex's); this covers the shared prompt text those routes append.
/// </summary>
public class DelegationSystemPromptTests
{
    [Fact]
    public void TheInstruction_PointsAtListProfiles_RatherThanNamingProfilesItself()
    {
        // The profiles and what they are good for live in the cockpit's settings and change there; restating them
        // in the prompt would hand the model a second copy that goes stale the moment a profile is edited.
        DelegationSystemPrompt.Default.Should().Contain("list_profiles");
        DelegationSystemPrompt.Default.Should().Contain("delegate_task");
        DelegationSystemPrompt.Default.Should().Contain("get_task_result");
    }
}
