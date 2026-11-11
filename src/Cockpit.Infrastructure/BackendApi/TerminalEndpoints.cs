using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Terminal;
using Cockpit.Core.Mcp;
using Cockpit.Core.Provisioning;
using Cockpit.Core.Terminal;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Infrastructure.Sessions.Tty;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Cockpit.Infrastructure.BackendApi;

// AC-2288: a server pty for a remote window, a plain shell or a TTY session's TUI. Every request passes the door again
// and is checked against the terminal it names, so a revoked key or a narrowed scope stops at its next keystroke.
// Nothing the terminal carries is logged: the routes hold bytes, never write them anywhere but the pty or the reader.
internal static class TerminalEndpoints
{
    public const string ShellRefusal = "An interactive shell on this server needs an admin key, and the organisation's policy must allow terminal and shell access.";

    public static void Map(RouteGroupBuilder api, IServiceProvider services)
    {
        api.MapPost("/terminals", async (OpenTerminalBody body) =>
        {
            var caller = _Caller();
            if (services.GetService<ServerTerminals>() is not { } terminals
                || services.GetService<ITtyLauncher>() is not { } launcher)
            {
                return Results.NotFound();
            }

            if (!_MayRunShell(caller, services))
            {
                return BackendApiRoutes.Error(StatusCodes.Status403Forbidden, "forbidden", ShellRefusal);
            }

            var shell = await _ServerShellAsync(services).ConfigureAwait(false);
            if (shell is null)
            {
                return BackendApiRoutes.Error(StatusCodes.Status409Conflict, "no_shell", "This server has no shell to open.");
            }

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var process = launcher.Launch(new ShellTtySessionProvider(shell), null, new Dictionary<string, string>(),
                _Size(body.Columns, 120), _Size(body.Rows, 40), home);
            var pty = new ServerPty(process);
            if (terminals.Add(caller.KeyPrefix!, shell.DisplayName, pty, caller.Revoked) is not { } opened)
            {
                await pty.DisposeAsync().ConfigureAwait(false);
                return BackendApiRoutes.Error(StatusCodes.Status409Conflict, "too_many_terminals", "This key already has as many terminals open on this server as it may.");
            }

            terminals.Detached(opened.Id);
            return Results.Json(new { id = opened.Id, name = opened.Name, node = Environment.MachineName, workingDirectory = home },
                statusCode: StatusCodes.Status201Created);
        }).RequireAdmin().Audited("open_terminal", services);

        api.MapGet("/terminals/{id}/output", async (HttpContext http, string id, long? after) =>
        {
            var caller = _Caller();
            if (await _FindAsync(id, caller, services).ConfigureAwait(false) is not { } pty)
            {
                http.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            http.Response.ContentType = "application/octet-stream";
            http.Response.Headers.CacheControl = "no-cache";
            await http.Response.StartAsync(http.RequestAborted).ConfigureAwait(false);
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted, caller.Revoked);
            await pty.CopyToAsync(http.Response.Body, after, stop.Token).ConfigureAwait(false);
            if (services.GetService<ServerTerminals>() is { } terminals && terminals.Find(id) is not null)
            {
                terminals.Detached(id);
            }
        }).RequireOperate();

        api.MapPost("/terminals/{id}/input", async (HttpContext http, string id) =>
        {
            if (http.Request.ContentLength is not { } length || length > ServerPty.MaxInputBytes)
            {
                return BackendApiRoutes.Error(StatusCodes.Status413PayloadTooLarge, "too_large", "Input goes in pieces of at most 64 KiB, with a length.");
            }

            if (await _FindAsync(id, _Caller(), services).ConfigureAwait(false) is not { } pty)
            {
                return Results.NotFound();
            }

            var bytes = new byte[length];
            await http.Request.Body.ReadExactlyAsync(bytes, http.RequestAborted).ConfigureAwait(false);
            await pty.WriteAsync(bytes, http.RequestAborted).ConfigureAwait(false);
            return Results.NoContent();
        }).RequireOperate();

        api.MapPost("/terminals/{id}/resize", async (string id, ResizeTerminalBody body) =>
        {
            if (await _FindAsync(id, _Caller(), services).ConfigureAwait(false) is not { } pty)
            {
                return Results.NotFound();
            }

            pty.Resize(body.Columns, body.Rows);
            return Results.NoContent();
        }).RequireOperate();

        // A TTY session ends the way any session does, with DELETE /sessions/{paneId}; this closes a shell.
        api.MapDelete("/terminals/{id}", async (string id) =>
            services.GetService<ServerTerminals>() is { } terminals
            && await _FindAsync(id, _Caller(), services).ConfigureAwait(false) is not null
            && await terminals.CloseAsync(id).ConfigureAwait(false)
                ? Results.NoContent()
                : Results.NotFound()).RequireAdmin().Audited("close_terminal", services);
    }

    // The pty `id` names, if this caller may reach it now: a shell only its own admin key under a policy that allows it,
    // a TTY session whoever may see that session. Anything else is the same 404 as a terminal that does not exist.
    private static async Task<ServerPty?> _FindAsync(string id, NodeCaller caller, IServiceProvider services)
    {
        if (services.GetService<ServerTerminals>()?.Find(id) is { } shell)
        {
            return shell.OwnerPrefix == caller.KeyPrefix && _MayRunShell(caller, services) ? shell.Pty : null;
        }

        if (services.GetService<ISessionRegistry>()?.Find(id) is not BackendTtySession { Pty: { } pty })
        {
            return null;
        }

        var policy = new NodeCallerSessionPolicy(
            services.GetRequiredService<IAssistantReadGateway>(),
            services.GetRequiredService<IAssistantAgentGateway>(),
            services.GetRequiredService<INodePairingBroker>(),
            services.GetRequiredService<ISessionProfileStore>(),
            services.GetService<IProfileLoginChecker>());
        return await policy.IsVisibleAsync(caller, id).ConfigureAwait(false) ? pty : null;
    }

    // A policy that cannot be read answers as forbidding both (IOrgPolicy.Current), so this fails closed.
    private static bool _MayRunShell(NodeCaller caller, IServiceProvider services)
    {
        var policy = services.GetService<IOrgPolicy>()?.Current;
        return caller.Capability == ConnectKeyCapability.Admin
            && !OrgPolicyCeilings.TerminalForbidden(policy)
            && !OrgPolicyCeilings.ShellForbidden(policy);
    }

    // The shell this server's own Options name, as a desktop resolves its terminal shell; else the OS default.
    private static async Task<ShellDescriptor?> _ServerShellAsync(IServiceProvider services)
    {
        var configured = services.GetService<ITerminalSettingsStore>() is { } store
            ? (await store.LoadAsync().ConfigureAwait(false)).Shell.Trim()
            : string.Empty;
        var shells = ShellCatalog.Detect();
        if (configured.Length == 0)
        {
            return shells.Count > 0 ? shells[0] : null;
        }

        return shells.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, configured, StringComparison.OrdinalIgnoreCase)
                || string.Equals(candidate.ExecutablePath, configured, StringComparison.OrdinalIgnoreCase))
            ?? ShellCatalog.ForCommand(configured)
            ?? (shells.Count > 0 ? shells[0] : null);
    }

    private static short _Size(int? value, short fallback) => (short)Math.Clamp(value ?? fallback, 2, 500);

    private static NodeCaller _Caller() =>
        McpRequestContext.CurrentNodeCaller ?? throw new InvalidOperationException("A terminal route ran without a connect-key caller.");
}

internal sealed record OpenTerminalBody(int? Columns = null, int? Rows = null);

internal sealed record ResizeTerminalBody(int Columns, int Rows);
