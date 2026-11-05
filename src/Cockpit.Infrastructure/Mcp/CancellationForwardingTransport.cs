using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Cockpit.Infrastructure.Mcp;

internal sealed class CancellationForwardingTransport(IClientTransport inner, ILogger logger) : IClientTransport
{
    public string Name => inner.Name;

    public async Task<ITransport> ConnectAsync(CancellationToken cancellationToken = default) =>
        new SessionTransport(await inner.ConnectAsync(cancellationToken), logger);

    private sealed class SessionTransport(ITransport transport, ILogger logger) : ITransport
    {
        private readonly McpBackgroundTasks _notifications = new(logger);
        public string? SessionId => transport.SessionId;
        public ChannelReader<JsonRpcMessage> MessageReader => transport.MessageReader;

        public async Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default)
        {
            try
            {
                await transport.SendMessageAsync(message, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && message is JsonRpcRequest { Method: "tools/call" })
            {
                // AC-2124: this failed send precedes SDK cancellation registration, so only this layer notifies.
                var request = (JsonRpcRequest)message;
                _notifications.Run(async () =>
                {
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    await transport.SendMessageAsync(new JsonRpcNotification
                    {
                        Method = "notifications/cancelled",
                        Params = JsonSerializer.SerializeToNode(new CancelledNotificationParams { RequestId = request.Id }),
                        Context = new JsonRpcMessageContext { RelatedTransport = request.Context?.RelatedTransport },
                    }, deadline.Token);
                });
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _notifications.DrainAsync();
            await transport.DisposeAsync();
        }
    }
}
