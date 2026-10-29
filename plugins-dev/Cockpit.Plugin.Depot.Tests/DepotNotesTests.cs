extern alias UiAsm;

using System.Net;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cockpit.Plugin.Depot.Contracts;
using Cockpit.Plugin.Depot.Model;
using Cockpit.Plugin.Depot.Notes;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.UI.Files;
using NSubstitute;
using DepotBrowserPanel = UiAsm::Cockpit.Plugin.Depot.UI.DepotBrowserPanel;
using DepotInboxModel = UiAsm::Cockpit.Plugin.Depot.UI.DepotInboxModel;
using DepotNotesModel = UiAsm::Cockpit.Plugin.Depot.UI.DepotNotesModel;
using UiConnection = UiAsm::Cockpit.Plugin.Depot.UI.DepotConnectionRegistration;

namespace Cockpit.Plugin.Depot.Tests;

[Collection("avalonia")]
public class DepotNotesTests
{
    // Layer 2, UI rendering: both palettes use the actual Notes tab, its theme and channel models over fake data.
    [Fact]
    public void NotesTab_RendersTheMockupInBothThemes_WithoutInvitations()
    {
        AvaloniaSynchronizationContext.InstallIfNeeded();
        var rig = _SceneRig();
        rig.Host.Storage.Get<List<UiConnection>>("connections").Returns([new UiConnection("a", "Work", "https://depot.example.com")]);
        Cockpit.Plugins.Abstractions.Docking.DockPanelRegistration? registered = null;
        rig.Host.When(host => host.AddDockPanel(Arg.Any<Cockpit.Plugins.Abstractions.Docking.DockPanelRegistration>()))
            .Do(call => registered = call.Arg<Cockpit.Plugins.Abstractions.Docking.DockPanelRegistration>());
        new UiAsm::Cockpit.Plugin.Depot.UI.DepotUi().InitializeUi(rig.Host);
        Dispatcher.UIThread.RunJobs();
        Assert.NotNull(registered);
        Assert.Equal("Depot", registered.Title);
        Assert.Equal(2, registered.Badge?.Count);
        Assert.Equal(SideMenuButtonBadgeTone.Success, registered.Badge?.Tone);
        rig.Host.Received().SetStatusText("● 2 live notes · Depot", SideMenuButtonBadgeTone.Success);
        var defaultBadge = new Cockpit.Plugins.Abstractions.Docking.DockPanelBadge();
        Assert.Equal(SideMenuButtonBadgeTone.Accent, defaultBadge.Tone);
        var changes = new List<string?>();
        defaultBadge.PropertyChanged += (_, change) => changes.Add(change.PropertyName);
        defaultBadge.Tone = SideMenuButtonBadgeTone.Success;
        defaultBadge.Tone = SideMenuButtonBadgeTone.Success;
        Assert.Equal(["Tone"], changes);
        var application = Application.Current ?? throw new InvalidOperationException("No Avalonia application.");
        _AssertRenderedTheme(registered, rig, ThemeVariant.Dark, "#FF949AA5");
        _AssertRenderedTheme(registered, rig, ThemeVariant.Light, "#FF5A616D");
        application.RequestedThemeVariant = ThemeVariant.Dark;
        _AssertInboxNavigationUsesTheSelectedConnection();
    }

    private static void _AssertRenderedTheme(Cockpit.Plugins.Abstractions.Docking.DockPanelRegistration registered,
        DepotBrowserModelTests.Rig rig, ThemeVariant theme, string headingColor)
    {
        var application = Application.Current ?? throw new InvalidOperationException("No Avalonia application.");
        application.RequestedThemeVariant = theme;
        var panel = Assert.IsType<DepotBrowserPanel>(registered.CreateView());
        var tabs = Assert.Single(Assert.IsType<DockPanel>(panel.Content).Children.OfType<TabControl>());
        tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(tab => Equals(tab.Header, "Notes"));
        var window = new Window { Width = 360, Height = 850, Content = panel };
        try
        {
            window.Show();
            _Until(() => panel.GetVisualDescendants().OfType<TextBlock>().Any(block => block.Text == "✎ 2026-10-12 Sprint review.md"));
            using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Nothing was rendered.");
            if (Environment.GetEnvironmentVariable("DEPOT_NOTES_SCENE_PNG") is { Length: > 0 } path)
            {
                using var stream = File.Create(Path.ChangeExtension(path, null) + "." + theme.Key.ToString()?.ToLowerInvariant() + ".png");
                frame.Save(stream, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            }

            var text = panel.GetVisualDescendants().OfType<TextBlock>().ToList();
            Assert.True(frame.PixelSize.Width > 0);
            Assert.Contains(text, block => block.Text == "Invited by Sanne");
            Assert.Contains(text, block => block.Text == "● live · 2");
            Assert.DoesNotContain(text, block => block.Text?.Contains("Invitations", StringComparison.OrdinalIgnoreCase) == true);
            var heading = Assert.Single(text, block => block.Text == "● LIVE NOW");
            Assert.Equal(11, heading.FontSize);
            Assert.Equal(FontWeight.SemiBold, heading.FontWeight);
            Assert.Equal(headingColor, heading.Foreground?.ToString()?.ToUpperInvariant());
            var title = Assert.Single(text, block => block.Text == "Sprint review");
            Assert.Equal(12.5, title.FontSize);
            Assert.Equal(FontWeight.SemiBold, title.FontWeight);
            Assert.Contains("Inter", title.FontFamily.Name, StringComparison.OrdinalIgnoreCase);
            var recentName = Assert.Single(text, block => block.Text == "✎ 2026-10-12 Sprint review.md");
            Assert.Equal(12.5, recentName.FontSize);
            Assert.Equal(FontWeight.Normal, recentName.FontWeight);
            Assert.Equal(["#FFE0A33E", "#FF5AA576", "#FFE0A33E"], panel.GetVisualDescendants().OfType<Border>()
                .Where(border => border.Width == 22 && border.Height == 22).Select(border => border.BorderBrush?.ToString()?.ToUpperInvariant()));
            Assert.Contains(text, block => block.Text == "1.1 KB");
            Assert.Equal(2, panel.GetVisualDescendants().OfType<Button>().Count(button => Equals(button.Content, "Join")));
            var create = panel.GetVisualDescendants().OfType<Button>().Single(button => button.Content is StackPanel content
                && content.Children.OfType<TextBlock>().Any(block => block.Text == "New meeting note"));
            create.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.Contains(rig.Host.ReceivedCalls(), call => call.GetMethodInfo().Name == "ShowDialogAsync"
                && Equals(call.GetArguments()[0], "New meeting note"));
            var link = panel.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "All in Inbox (3)"));
            link.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("Inbox", Assert.IsType<TabItem>(tabs.SelectedItem).Header);
        }
        finally
        {
            window.Close();
        }
    }

    // Layer 2, integration: the backend's real REST projection crosses the UI channel; invitations stay per connection.
    [Theory]
    [InlineData("a", "b")]
    public async Task Notes_UseOnlyThisConnectionsInvitation_ReplaceLists_AndClearOnSignInNeeded(string first, string second)
    {
        var rig = _SceneRig();
        var backendHost = Substitute.For<ICockpitHost>();
        IReadOnlyList<DepotLiveNote> listed = [_Live()];
        var refused = false;
        var structuredPeople = false;
        backendHost.CreateAuthorizedHandler(Arg.Any<string>(), Arg.Any<string>()).Returns(_ => new DepotBrowserModelTests.StubHandler(request =>
        {
            Assert.Equal("/cockpit/notes/live", request.RequestUri?.AbsolutePath);
            return new HttpResponseMessage(refused ? HttpStatusCode.Unauthorized : HttpStatusCode.OK)
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
        refused = true;
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

    private static void _AssertInboxNavigationUsesTheSelectedConnection()
    {
        var rig = _SceneRig();
        rig.Answer = (action, payload) => action switch
        {
            "notes-live" => new DepotNotesPage(payload.GetProperty("connectionId").GetString() ?? "a", "ok", [_Live()]),
            "inbox-list" => _Inbox(payload.GetProperty("connectionId").GetString() ?? "a", [_Invitation()]),
            _ => rig.Answers[action],
        };
        var panel = new DepotBrowserPanel(rig.Host, [new UiConnection("a", "Work", "https://depot.example.com"), new UiConnection("b", "Home", "https://home.example.com")]);
        var tabs = Assert.Single(Assert.IsType<DockPanel>(panel.Content).Children.OfType<TabControl>());
        var notesTab = tabs.Items.OfType<TabItem>().Single(tab => Equals(tab.Header, "Notes"));
        tabs.SelectedItem = notesTab;
        var window = new Window { Width = 420, Height = 720, Content = panel };
        try
        {
            window.Show();
            var notesPanel = Assert.IsType<UiAsm::Cockpit.Plugin.Depot.UI.DepotNotesPanel>(notesTab.Content);
            var selector = notesPanel.Header.GetVisualDescendants().OfType<ComboBox>().First();
            selector.SelectedIndex = 1;
            _Until(() => panel.GetVisualDescendants().OfType<Button>().Any(button => Equals(button.Content, "All in Inbox (3)")));
            panel.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "All in Inbox (3)"))
                .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            var inboxPanel = Assert.IsAssignableFrom<Control>(Assert.IsType<TabItem>(tabs.SelectedItem).Content);
            _Until(() => inboxPanel.GetVisualDescendants().OfType<ComboBox>().Any());
            Assert.Equal(1, Assert.Single(inboxPanel.GetVisualDescendants().OfType<ComboBox>()).SelectedIndex);
        }
        finally
        {
            window.Close();
        }
    }

    private static void _Until(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200 && !condition(); attempt++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(25);
        }
        Assert.True(condition(), "The Notes tab did not finish loading.");
    }
}
