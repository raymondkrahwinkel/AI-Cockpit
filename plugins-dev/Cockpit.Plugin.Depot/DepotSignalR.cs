using System.Net.WebSockets;
using Microsoft.AspNetCore.Http.Connections.Client;

namespace Cockpit.Plugin.Depot;

internal static class DepotSignalR
{
    public static void Configure(HttpConnectionOptions options, Func<HttpMessageHandler> handler)
    {
        options.HttpMessageHandlerFactory = _ => handler();
        options.WebSocketFactory = async (context, cancellationToken) =>
        {
            var socket = new ClientWebSocket();
            try
            {
                // SignalR's HTTP/1.1 default bypasses HttpMessageHandlerFactory; pass the host handler explicitly.
                using var invoker = new HttpMessageInvoker(handler());
                await socket.ConnectAsync(context.Uri, invoker, cancellationToken).ConfigureAwait(false);
                return socket;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        };
    }
}
