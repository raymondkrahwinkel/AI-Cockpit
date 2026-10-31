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

        // AC-1992: a picked brain is the same field, its source scheme, address and server kept exactly.
        const string Brain = "depot.acme:acme/zyra@depot.acme.example";
        Assert.Equal(Brain, SessionProfileEntry.FromDomain(profile with { Assistant = Brain }).ToDomain().Assistant);
    }

    [Fact]
    public void ToDomain_AProfileSavedBeforeThisTicket_ReadsAsNoAssistant()
    {
        var entry = new SessionProfileEntry { Label = "personal", ConfigDir = "~/.claude-personal" };

        Assert.Null(entry.ToDomain().Assistant);
    }
}
