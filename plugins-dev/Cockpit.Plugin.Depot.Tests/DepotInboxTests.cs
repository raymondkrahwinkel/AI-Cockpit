using System.Net;
using System.Text.Json;
using Cockpit.Plugin.Depot.Contracts;
using Cockpit.Plugin.Depot.Inbox;
using Cockpit.Plugin.Depot.Model;

namespace Cockpit.Plugin.Depot.Tests;

// AC-1586: the backend half of the Depot inbox. Each connection is its own inbox with its own hub; a refused sign-in
// leaves nothing behind; and what is shown is what Depot said last, never something it said before.
public class DepotInboxTests
{
    private static readonly DepotConnectionRegistration Work = new("a", "Work", "https://work.example.com");
    private static readonly DepotConnectionRegistration Home = new("b", "Home", "https://home.example.com");

    [Fact]
    public async Task APushOnOneConnection_RefreshesThatOneOnly_AndEachListIsItsOwn()
    {
        var depots = new Dictionary<string, _Depot> { [Work.Id] = new("w1", "w2"), [Home.Id] = new("h1") };
        var hubs = new Dictionary<string, _Hub>();
        await using var inbox = new DepotInbox(
            () => [Work, Home],
            connection => depots[connection.Id],
            (connection, _, onPush, onClosed) => hubs[connection.Id] = new _Hub(onPush),
            _ => { });

        var counts = await inbox.UnreadAsync(CancellationToken.None);
        hubs[Work.Id].Push(new DepotInboxPush(3, new Dictionary<string, int>()));
        await _Until(() => depots[Work.Id].ListFetches == 2);

        Assert.Equal([2, 1], counts.Select(count => count.Unread));
        Assert.Equal(1, depots[Home.Id].ListFetches);
        Assert.Equal(["w1", "w2"], (await inbox.ListAsync(new(Work.Id), CancellationToken.None)).Messages.Select(message => message.Id));
        Assert.Equal(["h1"], (await inbox.ListAsync(new(Home.Id), CancellationToken.None)).Messages.Select(message => message.Id));
    }

    // Acceptance 4: after Depot refuses the sign-in there is no message and no title in the model, and the hub is closed.
    [Fact]
    public async Task ASignInDepotRefuses_LeavesNoMessage_AndClosesTheHub()
    {
        var depot = new _Depot("w1") { Title = "Sprint review" };
        _Hub? hub = null;
        await using var service = new DepotInboxService(Work, depot, (_, _, onPush, _) => hub = new _Hub(onPush), _ => { });
        await service.RefreshAsync(CancellationToken.None);

        depot.Refuse = true;
        var page = await service.RefreshAsync(CancellationToken.None);

        Assert.Equal(DepotInboxState.Expired, page.Count.State);
        Assert.Empty(page.Messages);
        Assert.DoesNotContain("Sprint review", JsonSerializer.Serialize(service.Page), StringComparison.Ordinal);
        Assert.True(hub?.Disposed);
    }

    private static async Task _Until(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200 && !condition(); attempt++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), "The inbox did not reach the expected state.");
    }

    // One Depot's /cockpit/inbox, answering with the given message ids (every one a note invitation), or 401.
    internal sealed class _Depot(params string[] ids) : HttpMessageHandler
    {
        private int _listFetches;

        public int ListFetches => Volatile.Read(ref _listFetches);

        public bool Refuse { get; set; }

        public string? Title { get; set; }

        public string UserId { get; set; } = "u1";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Refuse)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            }

            object body = request.RequestUri?.AbsolutePath.EndsWith("/unread", StringComparison.Ordinal) == true
                ? new { userId = UserId, unread = ids.Length, perOrganization = new Dictionary<string, int>() }
                : _List();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent(body) });
        }

        private object _List()
        {
            Interlocked.Increment(ref _listFetches);
            return new
            {
                messages = ids.Select(id => new
                {
                    id,
                    kind = DepotInboxKind.NoteInvitation,
                    isRead = false,
                    handled = false,
                    createdAt = DateTimeOffset.UtcNow,
                    organization = (object?)null,
                    summary = "Sanne invites you",
                    openUrl = $"/mailbox?id={id}",
                    note = new { state = Title is null ? "noaccess" : "note", title = Title, path = (string?)null, live = false, openUrl = "/n/token" },
                }),
                nextCursor = (string?)null,
            };
        }

        private static StringContent JsonContent(object body) =>
            new(JsonSerializer.Serialize(body, JsonSerializerOptions.Web), System.Text.Encoding.UTF8, "application/json");
    }

    internal sealed class _Hub(Action<DepotInboxPush> onPush) : IDepotInboxHub
    {
        public bool Disposed { get; private set; }

        public void Push(DepotInboxPush push) => onPush(push);

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
