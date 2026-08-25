using Cockpit.App.ViewModels;
using Cockpit.Core.Profiles;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// AC-1071 acceptance criterion 8: the assistant a session under this profile runs as is editable here and
/// survives the round-trip. It lives on the profile because that is the half of the pair a session always has —
/// a session started without a project would otherwise get no assistant at all.
/// </summary>
public class EditableProfileViewModelAssistantTests
{
    private static SessionProfile Profile(string? assistant = null) =>
        new("personal", ClaudePluginProfile.Create("/home/r/.claude-personal", null)) { Assistant = assistant };

    [Fact]
    public void Load_SeedsTheAssistant_AndRoundTripsIt()
    {
        var editable = new EditableProfileViewModel(Profile("Zyra"), isLoggedIn: true);

        Assert.Equal("Zyra", editable.Assistant);
        Assert.Equal("Zyra", editable.ToProfile().Assistant);
    }

    [Fact]
    public void Load_AProfileWithNoAssistant_OpensWithAnEmptyField()
    {
        Assert.Equal(string.Empty, new EditableProfileViewModel(Profile(), isLoggedIn: true).Assistant);
    }

    [Fact]
    public void Save_CollapsesABlankAssistantToNull_AndTrimsWhatIsThere()
    {
        Assert.Null(new EditableProfileViewModel(Profile(), isLoggedIn: true) { Assistant = "   " }.ToProfile().Assistant);
        Assert.Equal("Aura", new EditableProfileViewModel(Profile(), isLoggedIn: true) { Assistant = "  Aura " }.ToProfile().Assistant);
    }
}
