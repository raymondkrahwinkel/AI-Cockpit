using System.Text.Json;
using System.Threading.Channels;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Cockpit.Infrastructure.Mcp;

internal sealed class CancellationForwardingTransport(IClientTransport inner) : IClientTransport
{
    public string Name => inner.Name;

    public async Task<ITransport> ConnectAsync(CancellationToken cancellationToken = default) =>
        new SessionTransport(await inner.ConnectAsync(cancellationToken));

    private sealed class SessionTransport(ITransport transport) : ITransport
    {
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
                // AC-2124: SDK 2.3 registers protocol cancellation only after the HTTP send, which can await SSE.
                var request = (JsonRpcRequest)message;
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try
                {
                    await transport.SendMessageAsync(new JsonRpcNotification
                    {
                        Method = "notifications/cancelled",
                        Params = JsonSerializer.SerializeToNode(new CancelledNotificationParams { RequestId = request.Id }),
                        Context = new JsonRpcMessageContext { RelatedTransport = request.Context?.RelatedTransport },
                    }, deadline.Token);
                }
                catch (Exception)
                {
                    // A disconnected upstream cannot receive cancellation; preserve the caller's cancellation.
                }
                throw;
            }
        }

        public ValueTask DisposeAsync() => transport.DisposeAsync();
    }
}
