using Cockpit.Core.Profiles;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Core.Tests.Sessions;

/// <summary>
/// AC-1071: the assistant is a machine-local profile field, so it has to survive a save/load of
/// <c>cockpit.json</c>'s profiles section, and a profile written before this ticket must read as unset.
/// </summary>
public class SessionProfileEntryAssistantTests
{
    private static SessionProfile Profile() => new("personal", new ClaudeConfig("~/.claude-personal"));

    [Fact]
    public void FromDomain_ThenToDomain_RoundTripsTheAssistant()
    {
        var profile = Profile() with { Assistant = "Zyra" };

        Assert.Equal("Zyra", SessionProfileEntry.FromDomain(profile).ToDomain().Assistant);
    }

    [Fact]
    public void FromDomain_ABlankAssistant_WritesNoField()
    {
        Assert.Null(SessionProfileEntry.FromDomain(Profile()).Assistant);
        Assert.Null(SessionProfileEntry.FromDomain(Profile() with { Assistant = "  " }).Assistant);
    }

    [Fact]
    public void ToDomain_AProfileSavedBeforeThisTicket_ReadsAsNoAssistant()
    {
        var entry = new SessionProfileEntry { Label = "personal", ConfigDir = "~/.claude-personal" };

        Assert.Null(entry.ToDomain().Assistant);
    }
}
