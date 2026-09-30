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
    public void Save_AnEmptyAllowList_PersistsAsNoList_NotAnEmptyOne()
    {
        var editable = new EditableProfileViewModel(TargetWith(new DelegationPolicy(AllowedAsTarget: true)), isLoggedIn: false)
        {
            AllowedTools = "   ",
        };

        Assert.Null(editable.ToProfile().DelegationPolicy.AllowedTools);
    }
}
