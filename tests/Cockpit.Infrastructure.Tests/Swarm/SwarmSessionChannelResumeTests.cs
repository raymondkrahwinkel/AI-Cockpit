using System.Net;
using System.Text;
using Cockpit.Core.Abstractions.Events;
using Cockpit.Infrastructure.BackendApi;

namespace Cockpit.Infrastructure.Tests.Swarm;

public sealed class SwarmSessionChannelResumeTests
{
    [Fact]
    public async Task ReconnectContinuesAfterTheLastRowWithoutAGapOrDuplicate()
    {
        var handler = new EventStreamHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://node.test/") };
        using var client = new BackendApiClient(http, TimeProvider.System, "swarm/session-events");
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var rows = await ReadAsync(client.StreamEventsAsync(null, stop.Token), 3, stop.Token);

        Assert.Equal([1L, 2L, 3L], rows.Select(row => row.Seq));
        Assert.Equal([null, "2"], handler.Cursors);
        Assert.All(handler.Paths, path => Assert.Equal("/swarm/session-events", path));
    }

    private static async Task<IReadOnlyList<BackendEvent>> ReadAsync(
        IAsyncEnumerable<BackendEvent> source,
        int count,
        CancellationToken cancellationToken)
    {
        var rows = new List<BackendEvent>();
        await foreach (var row in source.WithCancellation(cancellationToken))
        {
            rows.Add(row);
            if (rows.Count == count)
            {
                break;
            }
        }
        return rows;
    }

    private sealed class EventStreamHandler : HttpMessageHandler
    {
        private int _request;
        public List<string?> Cursors { get; } = [];
        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Cursors.Add(request.Headers.TryGetValues("Last-Event-ID", out var values) ? values.Single() : null);
            Paths.Add((request.RequestUri ?? throw new InvalidOperationException("Expected a value.")).AbsolutePath);
            var body = Interlocked.Increment(ref _request) == 1
                ? Frame(1, "one") + Frame(2, "two")
                : Frame(3, "three");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
            });
        }

        private static string Frame(long id, string text) =>
            $"id: {id}\nevent: row\ndata: {{\"PaneId\":\"pane\",\"text\":\"{text}\"}}\n\n";
    }
}
