extern alias UiAsm;

using System.Net;
using System.Text.Json;
using Cockpit.Plugin.Depot.Contracts;
using Cockpit.Plugin.Depot.Model;
using Cockpit.Plugin.Depot.Notes;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.UI.Files;
using NSubstitute;
using DepotInboxModel = UiAsm::Cockpit.Plugin.Depot.UI.DepotInboxModel;
using DepotNotesModel = UiAsm::Cockpit.Plugin.Depot.UI.DepotNotesModel;
using UiConnection = UiAsm::Cockpit.Plugin.Depot.UI.DepotConnectionRegistration;

namespace Cockpit.Plugin.Depot.Tests;

[Collection("avalonia")]
public class DepotNotesTests
{

    // Layer 2, integration: the backend's real REST projection crosses the UI channel; invitations stay per connection.
    [Theory]
    [InlineData("a", "b")]
    public async Task Notes_UseOnlyThisConnectionsInvitation_ReplaceLists_AndClearOnSignInNeeded(string first, string second)
    {
        var rig = _SceneRig();
        var backendHost = Substitute.For<ICockpitHost>();
        IReadOnlyList<DepotLiveNote> listed = [_Live()];
        var status = HttpStatusCode.OK;
        var structuredPeople = false;
        // AC-1958: like Depot's bearer group, the live list refuses the host-only depot.host token with 403.
        backendHost.CreateAuthorizedHandler(Arg.Any<string>(), Arg.Any<string>()).Returns(_ => new DepotBrowserModelTests.StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)));
        backendHost.CreateAuthorizedHandler(Arg.Any<string>()).Returns(_ => new DepotBrowserModelTests.StubHandler(request =>
        {
            Assert.Equal("/cockpit/notes/live", request.RequestUri?.AbsolutePath);
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    count = listed.Count,
                    notes = listed.Select(note => new
                    {
                        note.Address, note.Project, note.Path, note.Title, note.Href, note.Join,
                        people = note.People.Select(person => person.Name).ToArray(),
                        peopleDetails = structuredPeople ? note.People : null,
                    }),
                }, JsonSerializerOptions.Web)),
            };
        }));
        await using var backend = new DepotNotes(backendHost,
            () => [new DepotConnectionRegistration(first, "Work", "https://depot.example.com"), new DepotConnectionRegistration(second, "Home", "https://home.example.com")], _ => { });
        rig.Host.Channel.InvokeAsync("notes-live", Arg.Any<JsonElement>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            var id = call.ArgAt<JsonElement>(1).GetProperty("connectionId").GetString() ?? string.Empty;
            var page = await backend.ListAsync(new(id), CancellationToken.None);
            return JsonSerializer.SerializeToElement(page, JsonSerializerOptions.Web);
        });
        rig.Answer = (action, payload) => action == "inbox-list" && payload.GetProperty("connectionId").GetString() == first
                ? _Inbox(first, messages: []) : rig.Answers[action];
        rig.Answers["inbox-list"] = _Inbox(second, [_Invitation()]);
        using var inbox = new DepotInboxModel(rig.Host);
        using var notes = new DepotNotesModel(rig.Host, inbox);
        await inbox.ListAsync(first);
        await inbox.ListAsync(second);
        await notes.ListAsync(first);
        await notes.ListAsync(second);
        var note = Assert.Single(Assert.IsType<UiAsm::Cockpit.Plugin.Depot.Contracts.DepotNotesPage>(notes.PageOf(first)).Notes);
        Assert.Null(notes.InviterOf(first, note));
        Assert.Equal("Sanne", notes.InviterOf(second, note));
        Assert.Equal(2, notes.Total);
        Assert.Equal("Sanne", note.People[0].Name);
        Assert.Null(note.People[0].Color);
        structuredPeople = true;
        await notes.ListAsync(first);
        Assert.Equal("#E0A33E", notes.PageOf(first)?.Notes[0].People[0].Color);

        rig.Answers["inbox-list"] = _Inbox(second, [_Invitation("Not the recognized summary")]);
        await inbox.ListAsync(second);
        Assert.Null(notes.InviterOf(second, note));
        rig.Answers["inbox-list"] = _Inbox(second, [_Invitation("Other format", "Jithran")]);
        await inbox.ListAsync(second);
        Assert.Equal("Jithran", notes.InviterOf(second, note));

        listed = [];
        await notes.ListAsync(first);
        Assert.Empty(Assert.IsType<UiAsm::Cockpit.Plugin.Depot.Contracts.DepotNotesPage>(notes.PageOf(first)).Notes);
        listed = [_Live() with { Title = "Replacement", People = [new("Jithran")] }];
        await notes.ListAsync(first);
        Assert.Equal("Replacement", Assert.Single(Assert.IsType<UiAsm::Cockpit.Plugin.Depot.Contracts.DepotNotesPage>(notes.PageOf(first)).Notes).Title);
        status = HttpStatusCode.Forbidden;
        await notes.ListAsync(first);
        Assert.Equal("unreachable", notes.PageOf(first)?.State);
        Assert.Equal("Depot refused the live notes for this account.", notes.PageOf(first)?.Reason);
        status = HttpStatusCode.Unauthorized;
        await notes.ListAsync(first);
        Assert.Empty(Assert.IsType<UiAsm::Cockpit.Plugin.Depot.Contracts.DepotNotesPage>(notes.PageOf(first)).Notes);
        Assert.Equal("sign-in", notes.PageOf(first)?.State);
        Assert.DoesNotContain("Replacement", JsonSerializer.Serialize(notes.PageOf(first)), StringComparison.Ordinal);
        Assert.Equal(1, notes.Total);

        var launched = new List<Uri>();
        Task Launch(Uri uri)
        {
            launched.Add(uri);
            return Task.CompletedTask;
        }
        var connection = new UiConnection(first, "Work", "https://depot.example.com");
        await notes.JoinAsync(connection, note, Launch);
        await notes.NewAsync(connection, "krahwinkel-it/depot", Launch);
        Assert.Equal([new Uri("https://depot.example.com/n/token"), new Uri("https://depot.example.com/projects/krahwinkel-it/depot")], launched);

        // AC-1580: with the editor hook, Join starts the note's session and opens the note's own tab; a second Join asks for the same key.
        rig.Answers["note-live-open"] = true;
        notes.JoinNote = (joined, joinedNote) => UiAsm::Cockpit.Plugin.Depot.UI.DepotMarkdownFiles.OpenNoteAsync(rig.Host, joined, joinedNote.Address, joinedNote.Path, joinedNote.Join);
        rig.Answers["note-live-close"] = true;
        await notes.JoinAsync(connection, note, Launch);
        Assert.Equal(["note-live-open"], rig.Invokes.Select(call => call.Action).Where(action => action.StartsWith("note-live", StringComparison.Ordinal)));
        rig.Host.GetOpenWorkspaces("depot.markdown").Returns([new Cockpit.Plugins.Abstractions.Workspaces.OpenWorkspaceInstance("depot:a:krahwinkel-it/depot:Meetings/2026-10-19 Sprint review.md", false, false)]);
        await notes.JoinAsync(connection, note, Launch);
        Assert.Equal(2, launched.Count);
        Assert.Single(rig.Invokes, call => call.Action == "note-live-open");
        Assert.DoesNotContain(rig.Invokes, call => call.Action == "note-live-close");
        rig.Host.GetOpenWorkspaces("depot.markdown").Returns([]);
        rig.Host.OpenFileWorkspaceAsync("depot.markdown", Arg.Any<FileWorkspaceOpenOptions>()).Returns(Task.FromException(new InvalidOperationException("no tab")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => notes.JoinAsync(connection, note, Launch));
        Assert.Single(rig.Invokes, call => call.Action == "note-live-close");
        _ = rig.Host.Received(3).OpenFileWorkspaceAsync("depot.markdown", Arg.Is<FileWorkspaceOpenOptions>(options =>
            options.InstanceKey == "depot:a:krahwinkel-it/depot:Meetings/2026-10-19 Sprint review.md" && options.Edit));

        // The status bar's live-notes popup: one click on a row closes it, then opens the note's tab and starts the session.
        rig.Host.OpenFileWorkspaceAsync("depot.markdown", Arg.Any<FileWorkspaceOpenOptions>()).Returns(Task.CompletedTask);
        var closed = 0;
        var popup = new UiAsm::Cockpit.Plugin.Depot.UI.DepotLiveNotesPopup(rig.Host, notes, () => [new UiConnection(second, "Home", "https://home.example.com")], () => closed++);
        Avalonia.Threading.AvaloniaSynchronizationContext.InstallIfNeeded();
        new Avalonia.Controls.Window { Content = popup }.Show();
        var row = Assert.Single(Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(popup).OfType<Avalonia.Controls.Button>(), button => button.Classes.Contains("Ghost"));
        typeof(Avalonia.Controls.Button).GetMethod("OnClick", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(row, null);
        for (var wait = 0; wait < 100 && rig.Host.ReceivedCalls().Count(call => call.GetMethodInfo().Name == "OpenFileWorkspaceAsync") < 4; wait++)
        {
            await Task.Delay(20);
        }

        Assert.Equal(1, closed);
        _ = rig.Host.Received(4).OpenFileWorkspaceAsync("depot.markdown", Arg.Is<FileWorkspaceOpenOptions>(options => options.Edit));
        Assert.Equal(3, rig.Invokes.Count(call => call.Action == "note-live-open"));
    }

    private static DepotBrowserModelTests.Rig _SceneRig()
    {
        var rig = new DepotBrowserModelTests.Rig();
        rig.Answers["browser-projects"] = DepotBrowserModelTests._Ok(new[] { new { project = "krahwinkel-it/depot", name = "depot", role = "Editor" } });
        rig.Answers["browser-tree"] = DepotBrowserModelTests._Ok(new
        {
            nodes = new[]
            {
                new { path = "Meetings/2026-10-19 Sprint review.md", kind = "document", size = 1126, modified = "2026-10-19T10:00:00Z", contentType = "text/markdown" },
                new { path = "Meetings/2026-10-19 Daily standup.md", kind = "document", size = 1024, modified = "2026-10-19T09:30:00Z", contentType = "text/markdown" },
                new { path = "Meetings/2026-10-12 Sprint review.md", kind = "document", size = 1126, modified = "2026-10-12T10:00:00Z", contentType = "text/markdown" },
            },
            canShare = true,
        });
        rig.Answers["notes-live"] = new DepotNotesPage("a", "ok", [_Live(), _Live() with { Title = "Daily standup", Path = "Meetings/2026-10-19 Daily standup.md", Href = "/n/daily", People = [new("Sanne", "#E0A33E")] }]);
        rig.Answers["inbox-list"] = _Inbox("a", [_Invitation()]);
        rig.Answers["inbox-unread"] = new[] { new { connectionId = "a", state = "ok", unread = 3, perOrganization = new Dictionary<string, int>() } };
        rig.Host.GetProjectMemoryRowsAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<Cockpit.Plugins.Abstractions.Projects.ProjectMemoryRow>>([]));
        return rig;
    }

    private static DepotLiveNote _Live() => new("krahwinkel-it/depot", "depot", "Meetings/2026-10-19 Sprint review.md", "Sprint review", "/n/token", true, [new("Sanne", "#E0A33E"), new("Jithran", "#5AA576")]);

    private static object _Inbox(string id, object[] messages) => new
    {
        count = new { connectionId = id, state = "ok", unread = 3, perOrganization = new Dictionary<string, int>() },
        messages,
        updatedAt = DateTimeOffset.UtcNow,
    };

    private static object _Invitation(string summary = "Sanne invites you to a note", string? inviter = null) => new
    {
        id = "i1", kind = "NoteInvitation", isRead = false, handled = false, createdAt = DateTimeOffset.UtcNow,
        summary, inviter, openUrl = "https://depot.example.com/mailbox?id=i1",
        note = new { state = "note", title = "Sprint review", path = "Meetings/2026-10-19 Sprint review.md", live = true, openUrl = "https://depot.example.com/n/token" },
    };
}
