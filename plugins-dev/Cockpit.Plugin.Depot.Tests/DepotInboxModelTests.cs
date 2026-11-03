extern alias UiAsm;

using System.Text.Json;
using Cockpit.Plugin.Depot.Inbox;
using Cockpit.Plugins.Abstractions.Channels;
using Cockpit.Plugins.Abstractions.Notifications;
using NSubstitute;
using DepotInboxModel = UiAsm::Cockpit.Plugin.Depot.UI.DepotInboxModel;
using DepotInvitationNotifications = UiAsm::Cockpit.Plugin.Depot.UI.DepotInvitationNotifications;
using DepotConnectionRegistration = UiAsm::Cockpit.Plugin.Depot.UI.DepotConnectionRegistration;

namespace Cockpit.Plugin.Depot.Tests;

// Layer 2, channel/notification contract: inbox pages drive delivery, dedup, the Join route and privacy.
public sealed class DepotInboxModelTests
{
    [Fact]
    public async Task InvitationPushes_DeduplicatePerConnectionRecipientAndNote_AndJoinOnlyOnClick()
    {
        using var rig = new Rig();
        rig.Browser.Answers["inbox-unread"] = new object[]
        {
            new { connectionId = "a", state = "ok", unread = 2, perOrganization = new Dictionary<string, int>() },
            new { connectionId = "b", state = "unreachable", unread = 3, perOrganization = new Dictionary<string, int>() },
            new { connectionId = "c", state = "sign-in", unread = 0, perOrganization = new Dictionary<string, int>() },
        };
        await rig.Model.LoadCountsAsync();
        Assert.Equal(5, rig.Model.Total);
        rig.Browser.Invokes.Clear();

        rig.Push(_Page("a", "u1", "initial", noteLink: "old-token"));
        Assert.Empty(rig.Toasts);
        Assert.Empty(rig.System);
        rig.Push(_Page("a", "u1", "m1"));
        rig.Push(_Page("a", "u1", "m2"));
        Assert.Single(rig.Toasts);
        Assert.Single(rig.System);
        Assert.Empty(rig.Joined);
        Assert.Empty(rig.Browser.Invokes);
        Assert.Equal("Join", rig.Toasts[0].Label);
        Assert.False(rig.Model.PageOf("a")?.Messages.Single().IsRead);

        rig.Toasts[0].Click?.Invoke();
        rig.System[0].Click?.Invoke();
        Assert.Equal([new Uri("https://work.example.com/n/token"), new Uri("https://work.example.com/n/token")], rig.Joined);

        rig.Push(_Page("b", "u1", "initial", noteLink: "old-token"));
        Assert.Single(rig.Toasts);
        rig.Push(_Page("b", "u1", "m3"));
        rig.Push(_Page("a", "u2", "initial", noteLink: "old-token"));
        Assert.Equal(2, rig.Toasts.Count);
        rig.Push(_Page("a", "u2", "m4"));
        Assert.Equal(3, rig.Toasts.Count);
        Assert.Equal(3, rig.System.Count);
        Assert.Equal(4, rig.Model.Total);
        await _AssertRemovedTitlesStayPrivateAsync();
    }

    [Theory]
    [InlineData("NoteInvitation", 1, "https://work.example.com/n/token", "Sanne invited you to Sprint review", "Work · cockpit · Sanne, Alex")]
    [InlineData("AccessRequest", 0, "https://work.example.com/mailbox?id=m1", null, null)]
    public async Task OnlyUnreadInvitations_UseTheSilentSystemNotificationContract(
        string kind, int notificationCount, string openUrl, string? title, string? body)
    {
        using var rig = new Rig();
        rig.Seed("a", "u1");
        rig.Push(_Page("a", "u1", "m1", kind));

        Assert.Equal(notificationCount, rig.System.Count);
        Assert.Equal(notificationCount, rig.Toasts.Count);
        Assert.Equal(title, rig.System.SingleOrDefault().Title);
        Assert.Equal(body, rig.System.SingleOrDefault().Body);
        await DepotInboxModel.OpenAsync(
            rig.Model.PageOf("a")?.Messages.Single() ?? throw new InvalidOperationException("The message was not applied."),
            uri =>
            {
                rig.Joined.Add(uri);
                return Task.CompletedTask;
            });
        Assert.Equal([new Uri(openUrl)], rig.Joined);

        rig.Push(_Page("a", "u1", "ArtifactShared", "ArtifactShared"));
        rig.Push(_Page("a", "u1", "OrganizationInvitation", "OrganizationInvitation"));
        rig.Push(_Page("a", "u1", "ProjectInvitation", "ProjectInvitation"));

        rig.Push(_Page("a", "u1", "read", isRead: true));
        rig.Push(_Page("a", "u1", "handled", handled: true));
        rig.Push(_Page("a", "u1", "offline", state: "unreachable"));
        Assert.Equal(notificationCount, rig.System.Count);
        Assert.Equal(notificationCount, rig.Toasts.Count);
        Assert.Equal(2, rig.Model.Total);
    }

    private static async Task _AssertRemovedTitlesStayPrivateAsync()
    {
        using var rig = new Rig();
        rig.Seed("a", "u1");
        rig.Seed("a", "u2");
        var depot = new DepotInboxTests._Depot("w1") { Title = "Sprint review" };
        await using var service = new DepotInboxService(
            new Cockpit.Plugin.Depot.Model.DepotConnectionRegistration("a", "Work", "https://work.example.com"), depot,
            (_, _, onPush, _) => new DepotInboxTests._Hub(onPush), page => rig.Push(page));
        var before = await service.RefreshAsync(CancellationToken.None);
        Assert.Equal("Sprint review", before.Messages.Single().Note?.Title);
        Assert.Contains("Sprint review", Assert.Single(rig.System).Title);

        depot.Title = null;
        depot.UserId = "u2";
        var after = await service.RefreshAsync(CancellationToken.None);

        Assert.Null(after.Messages.Single().Note?.Title);
        Assert.Equal("u2", after.Recipient);
        Assert.Equal(2, rig.System.Count);
        Assert.Equal("Sanne invited you to a note", rig.System[^1].Title);
        Assert.Equal("Work", rig.System[^1].Body);
        Assert.Equal("Sanne invited you to a note\nWork", rig.Toasts[^1].Message);
        Assert.DoesNotContain("Sprint review", JsonSerializer.Serialize(rig.Model.PageOf("a")));
    }

    private static object _Page(string connection, string recipient, string id, string kind = "NoteInvitation",
        bool isRead = false, bool handled = false, string state = "ok", string noteLink = "token") => new
    {
        count = new { connectionId = connection, state, unread = 2, perOrganization = new Dictionary<string, int>() },
        messages = new[]
        {
            new
            {
                id, kind, isRead, handled, createdAt = DateTimeOffset.UtcNow, organization = (object?)null,
                summary = "Sanne invites you to a note", openUrl = "https://work.example.com/mailbox?id=" + id,
                note = new { state = "note", title = "Sprint review", path = "Meetings/sprint.md", live = true,
                    openUrl = "https://work.example.com/n/" + noteLink, project = "cockpit", participants = new[] { "Sanne", "Alex" } },
            },
        },
        updatedAt = DateTimeOffset.UtcNow,
        recipient,
    };

    private sealed class Rig : IDisposable
    {
        public DepotBrowserModelTests.Rig Browser { get; } = new();
        public DepotInboxModel Model { get; }
        public List<(string Message, string? Label, Action? Click)> Toasts { get; } = [];
        public List<(string Title, string Body, Action? Click)> System { get; } = [];
        public List<Uri> Joined { get; } = [];
        private Action<PluginChannelEvent>? _receive;

        public Rig()
        {
            Browser.Host.Channel.Subscribe("inbox-changed", Arg.Any<Action<PluginChannelEvent>>()).Returns(call =>
            {
                _receive = call.ArgAt<Action<PluginChannelEvent>>(1);
                return Substitute.For<IDisposable>();
            });
            Browser.Host.When(host => host.ShowToast(Arg.Any<string>(), Arg.Any<PluginToastSeverity>(), Arg.Any<string?>(), Arg.Any<Action?>()))
                .Do(call => Toasts.Add((call.ArgAt<string>(0), call.ArgAt<string?>(2), call.ArgAt<Action?>(3))));
            Browser.Host.When(host => host.ShowSystemNotification(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Action?>()))
                .Do(call => System.Add((call.ArgAt<string>(0), call.ArgAt<string>(1), call.ArgAt<Action?>(2))));
            Model = new DepotInboxModel(Browser.Host);
            var notifications = new DepotInvitationNotifications(Browser.Host,
                () => [new DepotConnectionRegistration("a", "Work", "https://work.example.com"), new DepotConnectionRegistration("b", "Home", "https://home.example.com")],
                (_, message) => DepotInboxModel.OpenAsync(message, uri =>
                {
                    Joined.Add(uri);
                    return Task.CompletedTask;
                }));
            Model.Changed += notifications.Show;
        }

        public void Push(object page) => _receive?.Invoke(new PluginChannelEvent("inbox-changed", 1, JsonSerializer.SerializeToElement(page, JsonSerializerOptions.Web)));

        public void Seed(string connection, string recipient) => Push(new
        {
            count = new { connectionId = connection, state = "ok", unread = 0, perOrganization = new Dictionary<string, int>() },
            messages = Array.Empty<object>(),
            updatedAt = DateTimeOffset.UtcNow,
            recipient,
        });

        public void Dispose() => Model.Dispose();
    }
}
