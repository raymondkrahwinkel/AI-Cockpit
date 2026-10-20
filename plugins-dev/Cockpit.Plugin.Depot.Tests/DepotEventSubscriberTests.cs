using System.Net;
using System.Text.Json;
using Cockpit.Plugin.Depot.Events;
using Cockpit.Plugin.Depot.Model;

namespace Cockpit.Plugin.Depot.Tests;

public class DepotEventSubscriberTests
{
    private static readonly DepotConnectionRegistration Connection = new("work", "Work", "https://work.example.com");

    [Fact]
    public async Task AnEvent_IsRoutedOnlyToConsumersOfItsTopic()
    {
        var hubs = new List<_Hub>();
        await using var subscriber = new DepotEventSubscriber(
            Connection,
            () => new _Depot(),
            (_, _, onEvent, onReconnected, onClosed) => _AddHub(hubs, onEvent, onReconnected, onClosed));
        var projects = new List<DepotEvent>();
        var definitions = new List<DepotEvent>();

        using var projectRegistration = await subscriber.SubscribeAsync("projects", projects.Add, _Noop, CancellationToken.None);
        using var definitionRegistration = await subscriber.SubscribeAsync("definitions", definitions.Add, _Noop, CancellationToken.None);
        var hub = Assert.Single(hubs);
        hub.Push(new DepotEvent("projects", "changed", "acme/roadmap", 1));

        Assert.Equal(["projects", "definitions"], hub.Topics);
        Assert.Single(projects);
        Assert.Empty(definitions);
    }

    [Fact]
    public async Task AVersionGapOrReconnect_ResyncsTheTopicConsumers()
    {
        var hubs = new List<_Hub>();
        await using var subscriber = new DepotEventSubscriber(
            Connection,
            () => new _Depot(),
            (_, _, onEvent, onReconnected, onClosed) => _AddHub(hubs, onEvent, onReconnected, onClosed));
        var resyncs = 0;

        using var registration = await subscriber.SubscribeAsync("projects", _Ignore, () => resyncs++, CancellationToken.None);
        var hub = Assert.Single(hubs);
        hub.Push(new DepotEvent("projects", "changed", "acme/roadmap", 1));
        hub.Push(new DepotEvent("projects", "changed", "acme/roadmap", 3));
        await hub.ReconnectAsync();

        Assert.Equal(2, resyncs);
    }

    private static void _Ignore(DepotEvent cockpitEvent)
    {
    }

    private static void _Noop()
    {
    }

    private static _Hub _AddHub(List<_Hub> hubs, Action<DepotEvent> onEvent, Func<Task> onReconnected, Func<Exception?, Task> onClosed)
    {
        var hub = new _Hub(onEvent, onReconnected, onClosed);
        hubs.Add(hub);
        return hub;
    }

    private sealed class _Depot : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { userId = "user-1" }), System.Text.Encoding.UTF8, "application/json"),
            });
    }

    private sealed class _Hub(Action<DepotEvent> onEvent, Func<Task> onReconnected, Func<Exception?, Task> onClosed) : IDepotEventHub
    {
        private readonly Func<Exception?, Task> _onClosed = onClosed;

        public List<string> Topics { get; } = [];

        public void Push(DepotEvent cockpitEvent)
        {
            onEvent(cockpitEvent);
        }

        public Task ReconnectAsync() => onReconnected();

        public Task CloseAsync(Exception? exception) => _onClosed(exception);

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SubscribeAsync(string topic, CancellationToken cancellationToken)
        {
            Topics.Add(topic);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
