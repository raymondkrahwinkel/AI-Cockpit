using Cockpit.Core.Profiles;
using Cockpit.Infrastructure.Configuration;

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

        Assert.Equal("acceptEdits", restored.PermissionCeiling);
        Assert.Equivalent(new object[] { "get_current_user", "search_issues" }, restored.AllowedTools);
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

        Assert.Equal(DelegationPolicy.DefaultPermissionCeiling, entry.ToDomain().PermissionCeiling);
    }
}
