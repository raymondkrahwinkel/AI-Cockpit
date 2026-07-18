using Cockpit.Core.Sessions.Permissions;
using FluentAssertions;

namespace Cockpit.Core.Tests.Permissions;

/// <summary>
/// The non-interactive tool-permission decision for a delegated session (AC-79). This is a security boundary —
/// a delegated local-model session runs tool calls with no human to say yes — so the classification and the
/// ceiling grading are pinned exhaustively: every (ceiling × class × allow-list) combination has a fixed,
/// deterministic outcome, and the safe reading of a missing/unknown signal is verified rather than assumed.
/// </summary>
public class DelegatedToolPermissionPolicyTests
{
    // --- Classify: MCP annotations → class ---

    [Theory]
    [InlineData(true, null, ToolPermissionClass.ReadOnly)]
    [InlineData(true, false, ToolPermissionClass.ReadOnly)]
    [InlineData(true, true, ToolPermissionClass.ReadOnly)]        // read-only wins: a read-only tool is not destructive
    [InlineData(false, false, ToolPermissionClass.Write)]
    [InlineData(false, true, ToolPermissionClass.Destructive)]
    [InlineData(false, null, ToolPermissionClass.Destructive)]    // non-read-only with no destructive hint → conservative
    [InlineData(null, null, ToolPermissionClass.Unknown)]
    [InlineData(null, false, ToolPermissionClass.Unknown)]        // no readOnlyHint at all → cannot tell, Unknown
    [InlineData(null, true, ToolPermissionClass.Unknown)]
    public void Classify_MapsAnnotationsToClass(bool? readOnlyHint, bool? destructiveHint, ToolPermissionClass expected)
    {
        DelegatedToolPermissionPolicy.Classify(readOnlyHint, destructiveHint).Should().Be(expected);
    }

    // --- Decide: read-only runs under every ceiling ---

    [Theory]
    [InlineData("plan")]
    [InlineData("default")]
    [InlineData("acceptEdits")]
    [InlineData("bypassPermissions")]
    public void Decide_ReadOnly_IsAllowedUnderEveryCeiling(string ceiling)
    {
        DelegatedToolPermissionPolicy.Decide(ceiling, ToolPermissionClass.ReadOnly, "search", onAllowList: false)
            .IsAllowed.Should().BeTrue();
    }

    // --- Decide: a write needs acceptEdits or bypass ---

    [Theory]
    [InlineData("plan", false)]
    [InlineData("default", false)]
    [InlineData("acceptEdits", true)]
    [InlineData("bypassPermissions", true)]
    public void Decide_Write_IsAllowedOnlyAtAcceptEditsOrBypass(string ceiling, bool expectedAllowed)
    {
        DelegatedToolPermissionPolicy.Decide(ceiling, ToolPermissionClass.Write, "write_file", onAllowList: false)
            .IsAllowed.Should().Be(expectedAllowed);
    }

    // --- Decide: a destructive tool needs bypass ---

    [Theory]
    [InlineData("plan", false)]
    [InlineData("default", false)]
    [InlineData("acceptEdits", false)]
    [InlineData("bypassPermissions", true)]
    public void Decide_Destructive_IsAllowedOnlyAtBypass(string ceiling, bool expectedAllowed)
    {
        DelegatedToolPermissionPolicy.Decide(ceiling, ToolPermissionClass.Destructive, "delete_repo", onAllowList: false)
            .IsAllowed.Should().Be(expectedAllowed);
    }

    // --- Decide: an unknown tool is denied unless allow-listed, at every ceiling short of the allow-list ---

    [Theory]
    [InlineData("plan")]
    [InlineData("default")]
    [InlineData("acceptEdits")]
    [InlineData("bypassPermissions")]
    public void Decide_Unknown_IsDeniedWhenNotOnAllowList(string ceiling)
    {
        var decision = DelegatedToolPermissionPolicy.Decide(ceiling, ToolPermissionClass.Unknown, "mystery_tool", onAllowList: false);

        decision.IsAllowed.Should().BeFalse();
        decision.DenyMessage.Should().NotBeNullOrWhiteSpace();
        decision.DenyMessage.Should().Contain("mystery_tool");
    }

    // --- Decide: the allow-list is the explicit yes and overrides class + ceiling ---

    [Theory]
    [InlineData(ToolPermissionClass.ReadOnly)]
    [InlineData(ToolPermissionClass.Write)]
    [InlineData(ToolPermissionClass.Destructive)]
    [InlineData(ToolPermissionClass.Unknown)]
    public void Decide_OnAllowList_IsAllowedRegardlessOfClassOrCeiling(ToolPermissionClass toolClass)
    {
        DelegatedToolPermissionPolicy.Decide("plan", toolClass, "trusted_tool", onAllowList: true)
            .IsAllowed.Should().BeTrue();
    }

    // --- Decide: an unrecognised/blank ceiling is treated as the most restrictive (read-only only) ---

    [Fact]
    public void Decide_UnrecognisedCeiling_AllowsOnlyReadOnly()
    {
        DelegatedToolPermissionPolicy.Decide("something-invented", ToolPermissionClass.ReadOnly, "search", onAllowList: false)
            .IsAllowed.Should().BeTrue();
        DelegatedToolPermissionPolicy.Decide("something-invented", ToolPermissionClass.Write, "write_file", onAllowList: false)
            .IsAllowed.Should().BeFalse();
        DelegatedToolPermissionPolicy.Decide(null, ToolPermissionClass.Write, "write_file", onAllowList: false)
            .IsAllowed.Should().BeFalse();
    }

    [Fact]
    public void Decide_DeniedWrite_ExplainsWithTheCeiling()
    {
        var decision = DelegatedToolPermissionPolicy.Decide("plan", ToolPermissionClass.Write, "write_file", onAllowList: false);

        decision.IsAllowed.Should().BeFalse();
        decision.DenyMessage.Should().Contain("write_file");
        decision.DenyMessage.Should().Contain("plan");
    }
}
