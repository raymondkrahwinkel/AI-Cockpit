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

    [Theory]
    [InlineData("write_file")]
    [InlineData("edit_file")]
    [InlineData("create_directory")]
    [InlineData("move_file")]
    public void ClassifyWellKnown_FilesystemWrites_AreWrite(string toolName)
    {
        Assert.Equal(ToolPermissionClass.Write, DelegatedToolPermissionPolicy.ClassifyWellKnown(toolName));
    }

    [Theory]
    [InlineData("read_file")]
    [InlineData("read_text_file")]
    [InlineData("read_media_file")]
    [InlineData("read_multiple_files")]
    [InlineData("list_directory")]
    [InlineData("list_directory_with_sizes")]
    [InlineData("directory_tree")]
    [InlineData("search_files")]
    [InlineData("get_file_info")]
    [InlineData("list_allowed_directories")]
    public void ClassifyWellKnown_FilesystemReads_AreReadOnly(string toolName)
    {
        Assert.Equal(ToolPermissionClass.ReadOnly, DelegatedToolPermissionPolicy.ClassifyWellKnown(toolName));
    }

    [Theory]
    [InlineData("mystery_tool")]
    [InlineData("delete_repo")]
    [InlineData("")]
    public void ClassifyWellKnown_UnrecognisedName_IsNull_SoAnnotationClassIsKept(string toolName)
    {
        Assert.Null(DelegatedToolPermissionPolicy.ClassifyWellKnown(toolName));
    }

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

    // --- Decide: read-only runs under every ceiling ---

    [Theory]
    [InlineData("plan")]
    [InlineData("default")]
    [InlineData("acceptEdits")]
    [InlineData("bypassPermissions")]
    public void Decide_ReadOnly_IsAllowedUnderEveryCeiling(string ceiling)
    {
        Assert.True(DelegatedToolPermissionPolicy.Decide(ceiling, ToolPermissionClass.ReadOnly, "search", onAllowList: false)
            .IsAllowed);
    }

    // --- Decide: a write needs acceptEdits or bypass ---

    [Theory]
    [InlineData("plan", false)]
    [InlineData("default", false)]
    [InlineData("acceptEdits", true)]
    [InlineData("bypassPermissions", true)]
    public void Decide_Write_IsAllowedOnlyAtAcceptEditsOrBypass(string ceiling, bool expectedAllowed)
    {
        Assert.Equal(expectedAllowed, DelegatedToolPermissionPolicy.Decide(ceiling, ToolPermissionClass.Write, "write_file", onAllowList: false)
            .IsAllowed);
    }

    // --- Decide: a destructive tool needs bypass ---

    [Theory]
    [InlineData("plan", false)]
    [InlineData("default", false)]
    [InlineData("acceptEdits", false)]
    [InlineData("bypassPermissions", true)]
    public void Decide_Destructive_IsAllowedOnlyAtBypass(string ceiling, bool expectedAllowed)
    {
        Assert.Equal(expectedAllowed, DelegatedToolPermissionPolicy.Decide(ceiling, ToolPermissionClass.Destructive, "delete_repo", onAllowList: false)
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
    public void Decide_UnrecognisedCeiling_AllowsOnlyReadOnly()
    {
        Assert.True(DelegatedToolPermissionPolicy.Decide("something-invented", ToolPermissionClass.ReadOnly, "search", onAllowList: false)
            .IsAllowed);
        Assert.False(DelegatedToolPermissionPolicy.Decide("something-invented", ToolPermissionClass.Write, "write_file", onAllowList: false)
            .IsAllowed);
        Assert.False(DelegatedToolPermissionPolicy.Decide(null, ToolPermissionClass.Write, "write_file", onAllowList: false)
            .IsAllowed);
    }

    [Fact]
    public void Decide_DeniedWrite_ExplainsWithTheCeiling()
    {
        var decision = DelegatedToolPermissionPolicy.Decide("plan", ToolPermissionClass.Write, "write_file", onAllowList: false);

        Assert.False(decision.IsAllowed);
        Assert.Contains("write_file", decision.DenyMessage);
        Assert.Contains("plan", decision.DenyMessage);
    }

    // --- Fail-safe defaults / collision reconciliation (security hardening) ---

    [Fact]
    public void Default_ToolPermissionClass_IsUnknown_SoAMissingClassFailsClosed()
    {
        // Unknown must be the zero value: a missing/uninitialised class must deny, not allow.
        Assert.Equal(ToolPermissionClass.Unknown, default(ToolPermissionClass));
        Assert.False(DelegatedToolPermissionPolicy.Decide("bypassPermissions", default, "x", onAllowList: false)
            .IsAllowed);
    }

    [Theory]
    // Same class → unchanged.
    [InlineData(ToolPermissionClass.ReadOnly, ToolPermissionClass.ReadOnly, ToolPermissionClass.ReadOnly)]
    // A safe name colliding with a riskier one takes the riskier (harder-to-run) class.
    [InlineData(ToolPermissionClass.ReadOnly, ToolPermissionClass.Write, ToolPermissionClass.Write)]
    [InlineData(ToolPermissionClass.ReadOnly, ToolPermissionClass.Destructive, ToolPermissionClass.Destructive)]
    [InlineData(ToolPermissionClass.Write, ToolPermissionClass.Destructive, ToolPermissionClass.Destructive)]
    // Unknown is the most restrictive of all — a collision with it can never be auto-run without the allow-list.
    [InlineData(ToolPermissionClass.ReadOnly, ToolPermissionClass.Unknown, ToolPermissionClass.Unknown)]
    [InlineData(ToolPermissionClass.Destructive, ToolPermissionClass.Unknown, ToolPermissionClass.Unknown)]
    public void MoreRestrictive_TakesTheHarderToRunClass_EitherOrder(ToolPermissionClass a, ToolPermissionClass b, ToolPermissionClass expected)
    {
        Assert.Equal(expected, DelegatedToolPermissionPolicy.MoreRestrictive(a, b));
        Assert.Equal(expected, DelegatedToolPermissionPolicy.MoreRestrictive(b, a));
    }

    // --- MoreRestrictiveCeiling: clamp a per-task requested ceiling to the profile's own (AC-117) ---

    [Theory]
    [InlineData("bypassPermissions", "acceptEdits", "acceptEdits")]
    [InlineData("acceptEdits", "default", "default")]
    [InlineData("acceptEdits", "plan", "plan")]
    [InlineData("default", "bypassPermissions", "default")]   // a request above the ceiling is clamped to the ceiling
    [InlineData("acceptEdits", "acceptEdits", "acceptEdits")]
    public void MoreRestrictiveCeiling_TakesTheLowerCeiling_EitherOrder(string a, string b, string expected)
    {
        Assert.Equal(expected, DelegatedToolPermissionPolicy.MoreRestrictiveCeiling(a, b));
        Assert.Equal(expected, DelegatedToolPermissionPolicy.MoreRestrictiveCeiling(b, a));
    }

    [Fact]
    public void MoreRestrictiveCeiling_AnUnrecognisedRequest_NeverWidens_AndDeniesAWrite()
    {
        // A per-task request the policy does not recognise must never widen what runs: it ranks as most
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

    [Fact]
    public void ClassifyAgentBuiltIn_AToolWeDoNotKnow_HasNoClass()
    {
        // Null, not a guess: the caller decides what an unclassifiable name means in its own context, and an MCP
        // tool must not be graded here by a name that happens to collide with a built-in.
        Assert.Null(DelegatedToolPermissionPolicy.ClassifyAgentBuiltIn("mcp__depot__write"));
        Assert.Null(DelegatedToolPermissionPolicy.ClassifyAgentBuiltIn("SomeFutureTool"));
    }

    [Fact]
    public void ClassifyAgentBuiltIn_AtTheReadOnlyDefault_ReadRuns_AndWriteAndBashDoNot()
    {
        // The whole point of the read-only default: a research task can still read the repository, and cannot
        // change it — not by a file write, and not by a shell command either.
        var ceiling = DelegatedToolPermissionPolicy.ReadOnlyCeiling;

        Assert.True(_Decide(ceiling, "Read").IsAllowed);
        Assert.False(_Decide(ceiling, "Write").IsAllowed);
        Assert.False(_Decide(ceiling, "Bash").IsAllowed);
    }

    [Theory]
    [InlineData("bypassPermissions", true)]
    [InlineData("acceptEdits", true)]
    [InlineData("default", false)]
    [InlineData("plan", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void AllowsChanges_IsTrueOnlyWhereATaskMayChangeSomething(string? ceiling, bool expected)
    {
        Assert.Equal(expected, DelegatedToolPermissionPolicy.AllowsChanges(ceiling));
    }

    private static PermissionDecision _Decide(string ceiling, string toolName) =>
        DelegatedToolPermissionPolicy.Decide(
            ceiling,
            DelegatedToolPermissionPolicy.ClassifyAgentBuiltIn(toolName) ?? ToolPermissionClass.Unknown,
            toolName,
            onAllowList: false);
}
