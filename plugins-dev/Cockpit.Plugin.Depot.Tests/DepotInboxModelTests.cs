extern alias UiAsm;

using DepotInboxMessage = UiAsm::Cockpit.Plugin.Depot.Contracts.DepotInboxMessage;
using DepotInboxModel = UiAsm::Cockpit.Plugin.Depot.UI.DepotInboxModel;
using DepotInboxNote = UiAsm::Cockpit.Plugin.Depot.Contracts.DepotInboxNote;

namespace Cockpit.Plugin.Depot.Tests;

// AC-1586: the UI half of the Depot inbox — the side-menu badge over every connection, and where a message opens.
public class DepotInboxModelTests
{
    // The badge adds up every connection Depot answered for; one that was never signed in shows no count of its own.
    [Fact]
    public async Task TheBadge_SumsTheUnreadOfEveryConnection()
    {
        var rig = new DepotBrowserModelTests.Rig();
        rig.Answers["inbox-unread"] = new object[]
        {
            new { connectionId = "a", state = "ok", unread = 2, perOrganization = new Dictionary<string, int>() },
            new { connectionId = "b", state = "unreachable", unread = 3, perOrganization = new Dictionary<string, int>() },
            new { connectionId = "c", state = "sign-in", unread = 0, perOrganization = new Dictionary<string, int>() },
        };
        using var model = new DepotInboxModel(rig.Host);

        await model.LoadCountsAsync();

        Assert.Equal(5, model.Total);
    }

    // Without an editor (AC-1580) a note invitation opens its note in Depot; every other kind opens its message there.
    [Theory]
    [InlineData("NoteInvitation", "https://work.example.com/n/token")]
    [InlineData("AccessRequest", "https://work.example.com/mailbox?id=m1")]
    public async Task AMessage_OpensInDepot_WhereItIsDecided(string kind, string expected)
    {
        var note = kind == "NoteInvitation" ? new DepotInboxNote("note", "Sprint review", null, true, "https://work.example.com/n/token") : null;
        var message = new DepotInboxMessage("m1", kind, false, false, DateTimeOffset.UtcNow, null, "Sanne invites you", "https://work.example.com/mailbox?id=m1", note);
        var launched = new List<Uri>();

        await DepotInboxModel.OpenAsync(message, uri =>
        {
            launched.Add(uri);
            return Task.CompletedTask;
        });

        Assert.Equal([new Uri(expected)], launched);
    }
}
