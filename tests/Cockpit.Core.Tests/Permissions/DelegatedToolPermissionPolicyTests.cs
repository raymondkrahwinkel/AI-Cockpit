using Cockpit.Core.Sessions.Permissions;

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
        Assert.Equal(expected, DelegatedToolPermissionPolicy.Classify(readOnlyHint, destructiveHint));
    }

    // --- ClassifyWellKnown: first-party fallback for annotation-less built-in tools (AC-100/AC-112) ---

    [Fact]
    public void WellKnownFilesystemWrite_RunsAtAcceptEdits_ButNotAtDefault()
    {
        // The AC-100/AC-112 fix, end-to-end at the policy layer: the filesystem preset ships no hint, so without
        // the fallback write_file is Unknown and blocked at every ceiling; with it, write_file is a Write and a
        // local coder profile at the default acceptEdits ceiling can finally write — while plan/default stay read-only.
        var toolClass = DelegatedToolPermissionPolicy.ClassifyWellKnown("write_file");
        Assert.Equal(ToolPermissionClass.Write, toolClass);

        Assert.True(DelegatedToolPermissionPolicy.Decide("acceptEdits", toolClass!.Value, "write_file", onAllowList: false)
            .IsAllowed);
        Assert.False(DelegatedToolPermissionPolicy.Decide("default", toolClass.Value, "write_file", onAllowList: false)
            .IsAllowed);
    }

    // --- Decide: a write needs acceptEdits or bypass, a destructive tool needs bypass ---

    [Theory]
    [InlineData(ToolPermissionClass.Write, "plan", false)]
    [InlineData(ToolPermissionClass.Write, "default", false)]
    [InlineData(ToolPermissionClass.Write, "acceptEdits", true)]
    [InlineData(ToolPermissionClass.Write, "bypassPermissions", true)]
    [InlineData(ToolPermissionClass.Destructive, "plan", false)]
    [InlineData(ToolPermissionClass.Destructive, "default", false)]
    [InlineData(ToolPermissionClass.Destructive, "acceptEdits", false)]
    [InlineData(ToolPermissionClass.Destructive, "bypassPermissions", true)]
    public void Decide_AWriteNeedsAcceptEdits_AndADestructiveToolNeedsBypass(ToolPermissionClass toolClass, string ceiling, bool expectedAllowed)
    {
        Assert.Equal(expectedAllowed, DelegatedToolPermissionPolicy.Decide(ceiling, toolClass, "a_tool", onAllowList: false)
            .IsAllowed);
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

        Assert.False(decision.IsAllowed);
        Assert.False(string.IsNullOrWhiteSpace(decision.DenyMessage));
        Assert.Contains("mystery_tool", decision.DenyMessage);
    }

    // --- Decide: the allow-list is the explicit yes and overrides class + ceiling ---

    [Theory]
    [InlineData(ToolPermissionClass.ReadOnly)]
    [InlineData(ToolPermissionClass.Write)]
    [InlineData(ToolPermissionClass.Destructive)]
    [InlineData(ToolPermissionClass.Unknown)]
    public void Decide_OnAllowList_IsAllowedRegardlessOfClassOrCeiling(ToolPermissionClass toolClass)
    {
        Assert.True(DelegatedToolPermissionPolicy.Decide("plan", toolClass, "trusted_tool", onAllowList: true)
            .IsAllowed);
    }

    // --- Decide: an unrecognised/blank ceiling is treated as the most restrictive (read-only only) ---

    [Fact]
    public void Decide_UnrecognisedCeiling_AllowsOnlyReadOnly_AndAnUnrecognisedRequestNeverWidens()
    {
        Assert.True(DelegatedToolPermissionPolicy.Decide("something-invented", ToolPermissionClass.ReadOnly, "search", onAllowList: false)
            .IsAllowed);
        Assert.False(DelegatedToolPermissionPolicy.Decide("something-invented", ToolPermissionClass.Write, "write_file", onAllowList: false)
            .IsAllowed);
        Assert.False(DelegatedToolPermissionPolicy.Decide(null, ToolPermissionClass.Write, "write_file", onAllowList: false)
            .IsAllowed);

        // AC-117: a per-task request the policy does not recognise must never widen what runs: it ranks as most
        // restrictive, and the resulting ceiling denies a write just like read-only does.
        var effective = DelegatedToolPermissionPolicy.MoreRestrictiveCeiling("acceptEdits", "nonsense");

        Assert.False(DelegatedToolPermissionPolicy.Decide(effective, ToolPermissionClass.Write, "write_file", onAllowList: false)
            .IsAllowed);
    }

    // --- ClassifyAgentBuiltIn / AllowsChanges: the tools a CLI runs itself (AC-971) ---

    [Theory]
    [InlineData("Read", ToolPermissionClass.ReadOnly)]
    [InlineData("Grep", ToolPermissionClass.ReadOnly)]
    [InlineData("WebFetch", ToolPermissionClass.ReadOnly)]
    [InlineData("Write", ToolPermissionClass.Write)]
    [InlineData("Edit", ToolPermissionClass.Write)]
    [InlineData("NotebookEdit", ToolPermissionClass.Write)]
    [InlineData("Bash", ToolPermissionClass.Destructive)]
    [InlineData("shell", ToolPermissionClass.Destructive)]              // Codex's name for the same thing
    [InlineData("apply_patch", ToolPermissionClass.Write)]
    public void ClassifyAgentBuiltIn_GradesTheToolsAnAgentCliRunsItself(string toolName, ToolPermissionClass expected)
    {
        Assert.Equal(expected, DelegatedToolPermissionPolicy.ClassifyAgentBuiltIn(toolName));
    }
}
