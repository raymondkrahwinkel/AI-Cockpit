using Cockpit.App.ViewModels;
using Cockpit.Core.Profiles;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// The delegation-policy arm of <see cref="EditableProfileViewModel"/> (AC-79): the permission ceiling and the
/// per-profile tool allow-list must survive the round-trip through the profile editor, since they are what the
/// non-interactive delegated gate reads. Without this the UI could show them yet quietly drop them on save — the
/// exact bug the hardcoded ceiling was before AC-79.
/// </summary>
public class EditableProfileViewModelDelegationTests
{
    private static SessionProfile TargetWith(DelegationPolicy policy) =>
        new("local", new OllamaConfig("http://localhost:11434", "llama3.1"), Delegation: policy);

    [Fact]
    public void Load_SeedsTheCeilingAndAllowListFromTheProfile()
    {
        var profile = TargetWith(new DelegationPolicy(
            AllowedAsTarget: true,
            PermissionCeiling: "plan",
            AllowedTools: ["get_current_user", "search_issues"]));

        var editable = new EditableProfileViewModel(profile, isLoggedIn: false);

        Assert.Equal("plan", editable.PermissionCeiling);
        Assert.Contains("get_current_user", editable.AllowedTools);
        Assert.Contains("search_issues", editable.AllowedTools);
    }

    [Fact]
    public void Save_RoundTripsTheCeilingAndAllowList()
    {
        var profile = TargetWith(new DelegationPolicy(
            AllowedAsTarget: true,
            PermissionCeiling: "plan",
            AllowedTools: ["get_current_user", "search_issues"]));
        var editable = new EditableProfileViewModel(profile, isLoggedIn: false);

        var policy = editable.ToProfile().DelegationPolicy;

        Assert.Equal("plan", policy.PermissionCeiling);
        Assert.Equivalent(new object[] { "get_current_user", "search_issues" }, policy.AllowedTools);
    }

    [Fact]
    public void Save_ReflectsEditsToTheCeilingAndAllowList()
    {
        var editable = new EditableProfileViewModel(TargetWith(new DelegationPolicy(AllowedAsTarget: true)), isLoggedIn: false);

        editable.PermissionCeiling = "bypassPermissions";
        editable.AllowedTools = "tool_a\ntool_b";

        var policy = editable.ToProfile().DelegationPolicy;
        Assert.Equal("bypassPermissions", policy.PermissionCeiling);
        Assert.Equivalent(new object[] { "tool_a", "tool_b" }, policy.AllowedTools);
    }

    [Fact]
    public void Save_AnEmptyAllowList_PersistsAsNoList_NotAnEmptyOne()
    {
        var editable = new EditableProfileViewModel(TargetWith(new DelegationPolicy(AllowedAsTarget: true)), isLoggedIn: false)
        {
            AllowedTools = "   ",
        };

        Assert.Null(editable.ToProfile().DelegationPolicy.AllowedTools);
    }
}
