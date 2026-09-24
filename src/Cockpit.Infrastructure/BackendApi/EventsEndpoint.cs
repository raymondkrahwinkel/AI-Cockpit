using System.Globalization;
using Cockpit.Core.Abstractions.Events;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Infrastructure.Mcp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Cockpit.Infrastructure.BackendApi;

internal static class EventsEndpoint
{
    public static void Map(RouteGroupBuilder api, IServiceProvider services)
    {
        api.MapGet("/events", (Func<HttpContext, Task>)(context => StreamAsync(context, services))).RequireOperate();
    }

    internal static async Task StreamAsync(HttpContext context, IServiceProvider services)
    {
        var cursor = context.Request.Headers["Last-Event-ID"].ToString();
        if (cursor.Length == 0)
        {
            cursor = context.Request.Query["after"].ToString();
        }

        if (cursor.Length > 0 && (!long.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed < 0))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var afterSeq = cursor.Length == 0 ? -1 : long.Parse(cursor, CultureInfo.InvariantCulture);
        var log = services.GetRequiredService<IBackendEventLog>();
        var caller = McpRequestContext.CurrentNodeCaller ?? throw new InvalidOperationException("An event route ran without a connect-key caller.");
        var sessions = services.GetRequiredService<ISessionRegistry>();
        var pairing = services.GetRequiredService<INodePairingBroker>();
        var time = services.GetService<TimeProvider>() ?? TimeProvider.System;
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        await context.Response.StartAsync(context.RequestAborted).ConfigureAwait(false);
        await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        using var heartbeat = new PeriodicTimer(TimeSpan.FromSeconds(15), time);
        await using var events = log.ReadFromAsync(afterSeq, stop.Token).GetAsyncEnumerator(stop.Token);
        Task<bool>? next = null;
        Task<bool>? tick = null;
        try
        {
            next = events.MoveNextAsync().AsTask();
            tick = heartbeat.WaitForNextTickAsync(stop.Token).AsTask();
            while (!stop.IsCancellationRequested)
            {
                var completed = await Task.WhenAny(next, tick).ConfigureAwait(false);
                if (completed == next)
                {
                    if (!await next.ConfigureAwait(false))
                    {
                        break;
                    }

                    var evt = events.Current;
                    if (evt.PaneId is { } paneId)
                    {
                        var session = sessions.Find(paneId);
                        if (session is null || !caller.AllowsSession(session.ActiveProfileLabel ?? string.Empty, session.ProjectId, pairing))
                        {
                            next = events.MoveNextAsync().AsTask();
                            continue;
                        }
                    }

                    var frame = $"id: {evt.Seq}\nevent: {evt.Kind}\ndata: {evt.Data.GetRawText()}\n\n";
                    await context.Response.WriteAsync(frame, stop.Token).ConfigureAwait(false);
                    await context.Response.Body.FlushAsync(stop.Token).ConfigureAwait(false);
                    next = events.MoveNextAsync().AsTask();
                }
                else
                {
                    if (!await tick.ConfigureAwait(false))
                    {
                        break;
                    }

                    await context.Response.WriteAsync(": ping\n\n", stop.Token).ConfigureAwait(false);
                    await context.Response.Body.FlushAsync(stop.Token).ConfigureAwait(false);
                    tick = heartbeat.WaitForNextTickAsync(stop.Token).AsTask();
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            if (next is not null)
            {
                try
                {
                    await next.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                {
                }
            }

            if (tick is not null)
            {
                try
                {
                    await tick.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                {
                }
            }
        }
    }
}
