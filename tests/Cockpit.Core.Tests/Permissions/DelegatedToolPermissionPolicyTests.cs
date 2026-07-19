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

    // --- ClassifyWellKnown: first-party fallback for annotation-less built-in tools (AC-100/AC-112) ---

    [Theory]
    [InlineData("write_file")]
    [InlineData("edit_file")]
    [InlineData("create_directory")]
    [InlineData("move_file")]
    public void ClassifyWellKnown_FilesystemWrites_AreWrite(string toolName)
    {
        DelegatedToolPermissionPolicy.ClassifyWellKnown(toolName).Should().Be(ToolPermissionClass.Write);
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
        DelegatedToolPermissionPolicy.ClassifyWellKnown(toolName).Should().Be(ToolPermissionClass.ReadOnly);
    }

    [Theory]
    [InlineData("mystery_tool")]
    [InlineData("delete_repo")]
    [InlineData("")]
    public void ClassifyWellKnown_UnrecognisedName_IsNull_SoAnnotationClassIsKept(string toolName)
    {
        DelegatedToolPermissionPolicy.ClassifyWellKnown(toolName).Should().BeNull();
    }

    [Fact]
    public void WellKnownFilesystemWrite_RunsAtAcceptEdits_ButNotAtDefault()
    {
        // The AC-100/AC-112 fix, end-to-end at the policy layer: the filesystem preset ships no hint, so without
        // the fallback write_file is Unknown and blocked at every ceiling; with it, write_file is a Write and a
        // local coder profile at the default acceptEdits ceiling can finally write — while plan/default stay read-only.
        var toolClass = DelegatedToolPermissionPolicy.ClassifyWellKnown("write_file");
        toolClass.Should().Be(ToolPermissionClass.Write);

        DelegatedToolPermissionPolicy.Decide("acceptEdits", toolClass!.Value, "write_file", onAllowList: false)
            .IsAllowed.Should().BeTrue();
        DelegatedToolPermissionPolicy.Decide("default", toolClass.Value, "write_file", onAllowList: false)
            .IsAllowed.Should().BeFalse();
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

    // --- Fail-safe defaults / collision reconciliation (security hardening) ---

    [Fact]
    public void Default_ToolPermissionClass_IsUnknown_SoAMissingClassFailsClosed()
    {
        // Unknown must be the zero value: a missing/uninitialised class must deny, not allow.
        default(ToolPermissionClass).Should().Be(ToolPermissionClass.Unknown);
        DelegatedToolPermissionPolicy.Decide("bypassPermissions", default, "x", onAllowList: false)
            .IsAllowed.Should().BeFalse();
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
        DelegatedToolPermissionPolicy.MoreRestrictive(a, b).Should().Be(expected);
        DelegatedToolPermissionPolicy.MoreRestrictive(b, a).Should().Be(expected, "the reconciliation is order-independent");
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
        DelegatedToolPermissionPolicy.MoreRestrictiveCeiling(a, b).Should().Be(expected);
        DelegatedToolPermissionPolicy.MoreRestrictiveCeiling(b, a).Should().Be(expected, "the clamp is order-independent");
    }

    [Fact]
    public void MoreRestrictiveCeiling_AnUnrecognisedRequest_NeverWidens_AndDeniesAWrite()
    {
        // A per-task request the policy does not recognise must never widen what runs: it ranks as most
        // restrictive, and the resulting ceiling denies a write just like read-only does.
        var effective = DelegatedToolPermissionPolicy.MoreRestrictiveCeiling("acceptEdits", "nonsense");

        DelegatedToolPermissionPolicy.Decide(effective, ToolPermissionClass.Write, "write_file", onAllowList: false)
            .IsAllowed.Should().BeFalse();
    }
}
