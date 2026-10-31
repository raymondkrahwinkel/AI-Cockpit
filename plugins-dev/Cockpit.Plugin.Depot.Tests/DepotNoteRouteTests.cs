extern alias UiAsm;

using Cockpit.Plugins.Abstractions.UI;
using Cockpit.Plugins.Abstractions.Workspaces;
using NSubstitute;
using UiContracts = UiAsm::Cockpit.Plugin.Depot.Contracts;
using UiRoute = UiAsm::Cockpit.Plugin.Depot.UI;

namespace Cockpit.Plugin.Depot.Tests;

public class DepotNoteRouteTests
{
    private const string Place = "krahwinkel-it/cockpit/Meetings/sprint.md";
    private static readonly UiRoute.DepotConnectionRegistration Work = new("a", "Work", "https://work.example.com");

    private static readonly UiContracts.DepotInboxMessage Invitation = new(
        "m1", "NoteInvitation", false, false, DateTimeOffset.UtcNow, null, "Sanne invites you to a note", "https://work.example.com/mailbox?id=m1",
        new UiContracts.DepotInboxNote("note", "Sprint review", "Meetings/sprint.md", true, "https://work.example.com/n/tok", "cockpit", ["Sanne"]));

    // Layer 2, plugin logic: the inbox, the Notes tab and a transcript link all end in the one route, with the same result.
    [Fact]
    public async Task Route_FromInboxNotesTabAndLink_OpensTheSameNote()
    {
        var live = new UiContracts.DepotLiveNote("krahwinkel-it/cockpit", "cockpit", "Meetings/sprint.md", "Sprint review", "/n/tok", true, []);
        var fromInbox = new Rig("ok", "editor");
        var fromNotes = new Rig("ok", "editor");
        var fromLink = new Rig("ok", "editor");

        await fromInbox.Route.JoinInvitationAsync(Work, Invitation);
        await fromNotes.Route.JoinLiveAsync(Work, live);
        await fromLink.Route.JoinLinkAsync("https://work.example.com/n/tok");

        Assert.Equal($"resolve:tok;live:{Place}:Edit;joined:m1", fromInbox.Seen);
        Assert.Equal($"resolve:tok;live:{Place}:Edit", fromNotes.Seen);
        Assert.Equal(fromNotes.Seen, fromLink.Seen);
    }

    // Layer 2, plugin logic: Depot's role decides the stand; an account without a right gets the bare "Shared note" tab.
    [Fact]
    public async Task Route_RoleOfTheAccount_DecidesTheStand()
    {
        var editor = new Rig("ok", "editor");
        var viewer = new Rig("ok", "viewer");
        var none = new Rig("unavailable", null);

        await editor.Route.JoinInvitationAsync(Work, Invitation);
        await viewer.Route.JoinInvitationAsync(Work, Invitation);
        await none.Route.JoinInvitationAsync(Work, Invitation);

        Assert.Equal($"live:{Place}:Edit", editor.Live);
        Assert.Equal($"live:{Place}:ViewOnly", viewer.Live);
        Assert.Equal(string.Empty, none.Live);
        Assert.Equal(UiRoute.DepotSharedNote.TabTitle, Assert.Single(none.Tabs).Title);
    }

    // Layer 2, no leak: the message knows the note's title and path, but an account without a right gets neither on the tab.
    [Fact]
    public async Task Route_WithoutARight_ShowsNeitherTitleNorPath()
    {
        var rig = new Rig("unavailable", null);

        await rig.Route.JoinInvitationAsync(Work, Invitation);

        var tab = Assert.Single(rig.Tabs);
        var shown = $"{tab.Title}|{tab.InstanceKey}";
        Assert.DoesNotContain("Sprint", shown, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Meetings", shown, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("resolve:tok", rig.Seen);
    }

    private sealed class Rig
    {
        private readonly List<string> _events = [];

        public Rig(string state, string? role)
        {
            var host = Substitute.For<ICockpitUiHost>();
            host.OpenWorkspaceAsync(Arg.Any<string>(), Arg.Any<WorkspaceOpenOptions>()).Returns(call =>
            {
                Tabs.Add(call.ArgAt<WorkspaceOpenOptions>(1));
                return Task.CompletedTask;
            });
            var answer = state == "ok"
                ? new UiContracts.DepotNoteLinkAnswer(state, "krahwinkel-it", "cockpit", "Meetings/sprint.md", "Sprint review", role)
                : new UiContracts.DepotNoteLinkAnswer(state);
            Route = new UiRoute.DepotNoteRoute(host, () => [Work],
                (_, token) =>
                {
                    _events.Add("resolve:" + token);
                    return Task.FromResult(answer);
                },
                _ => Task.FromResult(true),
                (_, messageId, _) =>
                {
                    _events.Add("joined:" + messageId);
                    return Task.CompletedTask;
                },
                _ => Task.CompletedTask)
            {
                OpenLive = (_, target) =>
                {
                    _events.Add($"live:{target.Organization}/{target.Project}/{target.Path}:{target.Stand}");
                    return Task.FromResult<Func<Task>>(() => Task.CompletedTask);
                },
            };
        }

        public UiRoute.DepotNoteRoute Route { get; }
        public List<WorkspaceOpenOptions> Tabs { get; } = [];
        public string Seen => string.Join(';', _events);
        public string Live => string.Join(';', _events.Where(entry => entry.StartsWith("live:", StringComparison.Ordinal)));
    }
}
