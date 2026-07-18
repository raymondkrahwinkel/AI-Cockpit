using Cockpit.Core.Profiles;
using Cockpit.Infrastructure.Configuration;
using FluentAssertions;

namespace Cockpit.Core.Tests.Configuration;

/// <summary>
/// The on-disk shape of a <see cref="DelegationPolicy"/> (AC-79 added the tool allow-list): a saved policy must
/// come back the same, and an old config that never had the allow-list must still load — the field is additive.
/// </summary>
public class DelegationPolicyEntryTests
{
    [Fact]
    public void RoundTrip_PreservesTheCeilingAndAllowList()
    {
        var policy = new DelegationPolicy(
            AllowedAsTarget: true,
            PermissionCeiling: "acceptEdits",
            AllowedTools: ["get_current_user", "search_issues"]);

        var restored = DelegationPolicyEntry.FromDomain(policy)!.ToDomain();

        restored.PermissionCeiling.Should().Be("acceptEdits");
        restored.AllowedTools.Should().BeEquivalentTo("get_current_user", "search_issues");
    }

    [Fact]
    public void ToDomain_WithNoAllowListEntry_LeavesItNull()
    {
        // An entry deserialized from an older config (no allowedTools key) has a null list, which must stay null
        // rather than becoming an empty list the decider would read differently.
        var entry = new DelegationPolicyEntry { AllowedAsTarget = true };

        entry.ToDomain().AllowedTools.Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ToDomain_WithABlankCeiling_CoercesToTheDefault_SoTheGateIsNeverDisarmed(string? ceiling)
    {
        // A hand-edited config with a null/blank ceiling must not leave a delegated session ungated (which would
        // hang it on a prompt nobody answers) — it coerces to the default ceiling.
        var entry = new DelegationPolicyEntry { AllowedAsTarget = true, PermissionCeiling = ceiling! };

        entry.ToDomain().PermissionCeiling.Should().Be(DelegationPolicy.DefaultPermissionCeiling);
    }
}
