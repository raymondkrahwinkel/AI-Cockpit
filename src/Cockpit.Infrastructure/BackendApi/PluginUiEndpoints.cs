using Cockpit.Infrastructure.Plugins;
using Cockpit.Plugins.Abstractions;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cockpit.Infrastructure.BackendApi;

internal static class PluginUiEndpoints
{
    internal static void Map(RouteGroupBuilder api, IServiceProvider services)
    {
        PluginUiCacheEndpoints.Map(api, services);
        api.MapGet("/plugins/{folderId}/ui/intents", (string folderId) =>
            services.GetService<PluginManager>()?.HostOf(folderId) is not null
                ? Results.Json(services.GetRequiredService<IPluginIntentRegistry>().Targets, ConnectKeyEndpoints.Json)
                : _NotRunning()).RequireAdmin();

        api.MapPost("/plugins/{folderId}/ui/{action}", async (string folderId, string action, UiHostRequest body, CancellationToken cancellationToken) =>
        {
            if (!_ValidIdentifier(action) || !_ValidIdentifier(body.PaneId) || !_ValidIdentifier(body.TargetPluginId)
                || !_ValidIdentifier(body.Action) || (body.Text is { } text && Encoding.UTF8.GetByteCount(text) > 64 * 1024)
                || (body.Data is { } data && (data.Count > 32 || data.Any(pair => pair.Key.Length > 128
                    || pair.Value is null || Encoding.UTF8.GetByteCount(pair.Value) > 8 * 1024))))
            {
                return BackendApiRoutes.Error(StatusCodes.Status400BadRequest, "invalid_request", "The plugin UI request exceeds its limits or contains an invalid identifier.");
            }
            if (services.GetService<PluginManager>()?.HostOf(folderId) is not { } host)
            {
                return _NotRunning();
            }
            try
            {
                return await _InvokeAsync(host, action, body, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                services.GetService<ILoggerFactory>()?.CreateLogger(nameof(PluginUiEndpoints))
                    .LogWarning("Plugin {FolderId}'s UI host action {Action} failed: {Failure}.", folderId, action, exception.GetType().Name);
                return BackendApiRoutes.Error(StatusCodes.Status500InternalServerError, "plugin_failed", "The plugin could not handle that UI action.");
            }
        }).RequireAdmin().Audited("plugin_ui_action", services);
    }

    private static bool _ValidIdentifier(string? value) => value is null || (value.Length <= 128 && !value.Any(char.IsControl));

    private static async Task<IResult> _InvokeAsync(ICockpitHost host, string action, UiHostRequest body, CancellationToken cancellationToken)
    {
        switch (action)
        {
            case "intent" when !string.IsNullOrWhiteSpace(body.TargetPluginId) && !string.IsNullOrWhiteSpace(body.Action)
                && body.Data is not null && body.Data.All(pair => !string.IsNullOrEmpty(pair.Key) && pair.Value is not null):
                // The running host stamps the caller's plugin id; the request only names the destination.
                var result = await host.SendIntent(body.TargetPluginId, body.Action, body.Data).ConfigureAwait(false);
                return result is null
                    ? BackendApiRoutes.Error(StatusCodes.Status404NotFound, "no_action", "That plugin no longer offers this intent on the server.")
                    : Results.Json(result, ConnectKeyEndpoints.Json);
            case "send-to-session" when !string.IsNullOrWhiteSpace(body.PaneId) && !string.IsNullOrEmpty(body.Text):
                var sent = await host.SendToSessionAsync(body.PaneId, body.Text, []).ConfigureAwait(false);
                return sent.Delivered ? Results.Json(new { ok = true }, ConnectKeyEndpoints.Json) : _NoSession();
            case "insert-into-session" when !string.IsNullOrWhiteSpace(body.PaneId) && !string.IsNullOrEmpty(body.Text):
                return await host.InsertIntoSessionAsync(body.PaneId, body.Text).ConfigureAwait(false)
                    ? Results.Json(new { ok = true }, ConnectKeyEndpoints.Json) : _NoSession();
            case "project-memory":
                var rows = await host.GetProjectMemoryRowsAsync(body.PaneId, cancellationToken).ConfigureAwait(false);
                return Results.Json(new { rows }, ConnectKeyEndpoints.Json);
            default:
                return BackendApiRoutes.Error(StatusCodes.Status400BadRequest, "invalid_request", "That plugin UI host action is invalid.");
        }
    }

    private static IResult _NotRunning() =>
        BackendApiRoutes.Error(StatusCodes.Status404NotFound, "no_plugin", "That plugin is not running on the server.");

    private static IResult _NoSession() =>
        BackendApiRoutes.Error(StatusCodes.Status409Conflict, "session_unavailable", "The server session did not accept that action.");

    internal sealed record UiHostRequest(string? TargetPluginId, string? Action, IReadOnlyDictionary<string, string>? Data, string? PaneId, string? Text);
}
