using System.Text.Json;
using System.Text.Json.Serialization;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Mcp;
using Cockpit.Infrastructure.Mcp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Cockpit.Infrastructure.BackendApi;

// AC-2282: the server's MCP registry as the ordinary MCP Servers page edits it. Only the rows that page shows cross:
// node wiring and the cockpit's own servers stay with their source. Secrets are write-only and keep their address.
internal static class McpRegistryEndpoints
{
    private const string Route = "/mcp/servers";

    private const string SignInKey = "registry";

    private static readonly JsonSerializerOptions Strict = new(ConnectKeyEndpoints.Json)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static void Map(RouteGroupBuilder api, IServiceProvider services)
    {
        IMcpServerStore store() => services.GetRequiredService<IMcpServerStore>();

        api.MapGet(Route, async (CancellationToken ct) =>
            Results.Json(ToWire(await _EditableAsync(services, ct).ConfigureAwait(false)), ConnectKeyEndpoints.Json))
            .RequireAdmin().Audited("list_mcp_servers", services);

        api.MapPut(Route, async (HttpRequest request, CancellationToken ct) =>
        {
            McpServersBody? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<McpServersBody>(request.Body, Strict, ct).ConfigureAwait(false);
            }
            catch (JsonException)
            {
                body = null;
            }

            if (body?.Servers is null)
            {
                return _Invalid("The body holds only servers, each with the fields the MCP Servers page edits.");
            }

            await OptionsSettingsEndpoints.EditGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (await OptionsSettingsEndpoints.RefuseIfNoLongerAdminAsync(request.HttpContext, services).ConfigureAwait(false) is { } revoked)
                {
                    return revoked;
                }

                var stored = await store().LoadAsync(ct).ConfigureAwait(false);
                var (merged, refusal) = Merge(stored, body.Servers, _InternalNames(services));
                if (merged is null)
                {
                    return refusal;
                }

                await store().SaveAsync(merged, ct).ConfigureAwait(false);
                return Results.Json(ToWire(await _EditableAsync(services, ct).ConfigureAwait(false)), ConnectKeyEndpoints.Json);
            }
            finally
            {
                OptionsSettingsEndpoints.EditGate.Release();
            }
        }).RequireAdmin().Audited("change_mcp_servers", services);

        api.MapGet(Route + "/{id}/auth-state", async (string id, CancellationToken ct) =>
            await _OAuthRowAsync(services, id, ct).ConfigureAwait(false) is { } server
                ? Results.Json(new { state = await services.GetRequiredService<IMcpOAuthCoordinator>().GetStateAsync(server, ct).ConfigureAwait(false) }, ConnectKeyEndpoints.Json)
                : Results.NotFound()).RequireAdmin();

        api.MapPost(Route + "/{id}/sign-out", async (string id, CancellationToken ct) =>
        {
            if (await _OAuthRowAsync(services, id, ct).ConfigureAwait(false) is not { } server)
            {
                return Results.NotFound();
            }

            await services.GetRequiredService<IMcpOAuthCoordinator>().SignOutAsync(server, ct).ConfigureAwait(false);
            return Results.Json(new { }, ConnectKeyEndpoints.Json);
        }).RequireAdmin().Audited("sign_out_mcp_server", services);

        // The browser step runs on the desktop, the token stays here: the same flow a session's or a plugin's server uses.
        const string signIn = Route + "/{id}/sign-in";
        McpServerSignIns signIns() => services.GetRequiredService<McpServerSignIns>();
        api.MapPost(signIn, async (string id, McpSignInStartBody body, CancellationToken ct) =>
        {
            if (body.Port is < 1024 or > 65535)
            {
                return _Invalid("A loopback port from 1024 to 65535 is required.");
            }

            if (await _OAuthRowAsync(services, id, ct).ConfigureAwait(false) is not { } server)
            {
                return Results.NotFound();
            }

            var flow = await signIns().StartAsync(SignInKey, id, body.Port, async signInCt =>
            {
                var access = await services.GetRequiredService<IMcpOAuthCoordinator>().AcquireAsync(server, interactive: true, signInCt).ConfigureAwait(false);
                return access.State == McpAuthState.Authorized
                    ? Cockpit.Plugins.Abstractions.Mcp.PluginMcpSignInOutcome.Authorized
                    : Cockpit.Plugins.Abstractions.Mcp.PluginMcpSignInOutcome.Declined;
            }, _ => Task.CompletedTask, ct).ConfigureAwait(false);
            return Results.Json(PluginEndpoints.McpSignInView(flow), ConnectKeyEndpoints.Json, statusCode: StatusCodes.Status201Created);
        }).RequireAdmin().Audited("start_mcp_server_sign_in", services);

        api.MapPost(signIn + "/{flowId}/input", (string id, string flowId, SignInInputBody body) =>
            !string.IsNullOrWhiteSpace(body.Text) && signIns().Find(SignInKey, id, flowId) is { } flow && flow.Submit(body.Text)
                ? Results.Json(PluginEndpoints.McpSignInView(flow), ConnectKeyEndpoints.Json)
                : Results.NotFound()).RequireAdmin().Audited("send_mcp_server_sign_in_redirect", services);
        api.MapGet(signIn + "/{flowId}", (string id, string flowId) =>
            signIns().Find(SignInKey, id, flowId) is { } flow
                ? Results.Json(PluginEndpoints.McpSignInView(flow), ConnectKeyEndpoints.Json)
                : Results.NotFound()).RequireAdmin();
        api.MapDelete(signIn + "/{flowId}", (string id, string flowId) =>
        {
            signIns().Find(SignInKey, id, flowId)?.End("cancelled");
            return Results.Json(new { }, ConnectKeyEndpoints.Json);
        }).RequireAdmin().Audited("end_mcp_server_sign_in", services);
    }

    // What the MCP Servers page lists: neither node wiring (the Servers page's) nor a name the cockpit serves itself.
    internal static bool IsEditable(McpServerConfig server, IReadOnlySet<string> internalNames) =>
        !server.IsNodeConnection && !server.Internal && !server.CockpitHosted && !internalNames.Contains(server.Name.Trim());

    // The server's answer reads every secret as the marker; the window's edit sends what the page holds.
    internal static IReadOnlyList<McpServerWire> ToWire(IEnumerable<McpServerConfig> servers, bool redact = true) =>
    [
        .. servers.Select(server => new McpServerWire(
            server.Id, server.Name, server.Transport, server.Scope, server.Command, server.Args, server.Url, server.Auth,
            redact && !string.IsNullOrEmpty(server.ApiKey) ? ProfileEndpoints.KeptSecret : server.ApiKey,
            server.OAuthAuthority, server.OAuthClientId, server.OAuthScopes,
            redact ? [.. server.Headers.Select(header => header with { Value = ProfileEndpoints.KeptSecret })] : server.Headers,
            server.Enabled)),
    ];

    // The edited rows in place of the editable ones; every other row stays as stored. A kept secret is put back only
    // while its row keeps its URL, and what the page never edits (pinning, plugin wiring) is carried from the stored row.
    internal static (List<McpServerConfig>? Servers, IResult Refusal) Merge(IReadOnlyList<McpServerConfig> stored, IReadOnlyList<McpServerWire> edited, IReadOnlySet<string> internalNames)
    {
        var editable = stored.Where(server => IsEditable(server, internalNames)).ToDictionary(server => server.Id, StringComparer.Ordinal);
        var kept = stored.Where(server => !IsEditable(server, internalNames)).ToList();
        var merged = new List<McpServerConfig>();
        foreach (var wire in edited)
        {
            if (_Refusal(wire) is { } invalid)
            {
                return (null, _Invalid(invalid));
            }

            if (kept.Any(server => server.Id == wire.Id))
            {
                return (null, _Invalid($"\"{wire.Name.Trim()}\" is not a server this page edits."));
            }

            editable.TryGetValue(wire.Id, out var held);
            var sameAddress = held is not null && string.Equals(held.Url, wire.Url, StringComparison.Ordinal);
            string? apiKey = wire.ApiKey;
            if (apiKey == ProfileEndpoints.KeptSecret)
            {
                if (!sameAddress || string.IsNullOrEmpty(held!.ApiKey))
                {
                    return (null, ProfileEndpoints.KeyAgain);
                }

                apiKey = held.ApiKey;
            }

            var headers = new List<McpHeader>();
            foreach (var header in wire.Headers ?? [])
            {
                if (header.Value != ProfileEndpoints.KeptSecret)
                {
                    headers.Add(header);
                }
                else if (sameAddress && held!.Headers.FirstOrDefault(old => string.Equals(old.Name, header.Name, StringComparison.OrdinalIgnoreCase)) is { } old)
                {
                    headers.Add(header with { Value = old.Value });
                }
                else
                {
                    return (null, ProfileEndpoints.KeyAgain);
                }
            }

            merged.Add(new McpServerConfig
            {
                Id = wire.Id,
                Name = wire.Name.Trim(),
                Transport = wire.Transport,
                Scope = wire.Scope,
                Command = wire.Command,
                Args = wire.Args ?? [],
                Url = wire.Url,
                Auth = wire.Auth,
                ApiKey = apiKey,
                OAuthAuthority = wire.OAuthAuthority,
                OAuthClientId = wire.OAuthClientId,
                OAuthScopes = wire.OAuthScopes,
                Headers = headers,
                Enabled = wire.Enabled,
                OAuthHostOnlyScopes = held?.OAuthHostOnlyScopes,
                IncludeAgentName = held?.IncludeAgentName ?? false,
                PinnedCertificateFingerprint = sameAddress ? held!.PinnedCertificateFingerprint : null,
                KeyExpiresAt = sameAddress && apiKey == held!.ApiKey ? held.KeyExpiresAt : null,
            });
        }

        var taken = kept.Select(server => server.Name.Trim()).Concat(internalNames).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (merged.Select(server => server.Id).Distinct(StringComparer.Ordinal).Count() != merged.Count
            || merged.GroupBy(server => server.Name, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1)
            || merged.Any(server => taken.Contains(server.Name)))
        {
            return (null, _Invalid("Every server needs its own name, also beside the servers the cockpit runs itself."));
        }

        return ([.. merged, .. kept], Results.Empty);
    }

    // Transport, command and headers as the server will use them: one shape per transport, nothing that splits a line.
    private static string? _Refusal(McpServerWire server)
    {
        if (string.IsNullOrWhiteSpace(server.Id) || string.IsNullOrWhiteSpace(server.Name) || _HasControl(server.Id) || _HasControl(server.Name)
            || !Enum.IsDefined(server.Transport) || !Enum.IsDefined(server.Scope) || !Enum.IsDefined(server.Auth))
        {
            return "Every server needs an id, a name, a transport, a scope and an authentication mode.";
        }

        var oauth = server.OAuthAuthority ?? server.OAuthClientId ?? server.OAuthScopes;
        if (server.Transport == McpTransport.Stdio)
        {
            return string.IsNullOrWhiteSpace(server.Command) || _HasControl(server.Command) || (server.Args ?? []).Any(arg => arg is null || arg.Contains('\0'))
                || server.Url is not null || server.Auth != McpServerAuth.None || server.ApiKey is not null || oauth is not null || server.Headers is { Count: > 0 }
                ? $"\"{server.Name.Trim()}\" runs over stdio: it needs a command, and takes no URL, authentication or headers."
                : null;
        }

        if (!Uri.TryCreate(server.Url, UriKind.Absolute, out var url) || url.Scheme is not ("https" or "http")
            || server.Command is not null || server.Args is { Count: > 0 })
        {
            return $"\"{server.Name.Trim()}\" runs over HTTP: it needs an http(s) URL, and takes no command.";
        }

        if ((server.ApiKey is not null && server.Auth != McpServerAuth.ApiKey) || (oauth is not null && server.Auth != McpServerAuth.OAuth)
            || (server.ApiKey is { } key && (key.Length == 0 || _HasControl(key))))
        {
            return $"\"{server.Name.Trim()}\" holds a key or OAuth settings its authentication mode does not use.";
        }

        return (server.Headers ?? []).Any(header => header?.Name is not { Length: > 0 } name || header.Value is not { Length: > 0 } value
                || !name.All(_IsTokenChar) || _HasControl(value))
            ? $"\"{server.Name.Trim()}\" has a header without a valid name or value."
            : null;
    }

    private static bool _HasControl(string text) => text.Any(char.IsControl);

    private static bool _IsTokenChar(char c) => char.IsAsciiLetterOrDigit(c) || "!#$%&'*+-.^_`|~".Contains(c);

    private static IReadOnlySet<string> _InternalNames(IServiceProvider services) =>
        services.GetServices<ICockpitInternalMcpProvider>().SelectMany(provider =>
        {
            try
            {
                return provider.GetServers().Select(server => server.Name.Trim());
            }
            catch
            {
                return [];
            }
        }).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static async Task<IReadOnlyList<McpServerConfig>> _EditableAsync(IServiceProvider services, CancellationToken ct)
    {
        var internalNames = _InternalNames(services);
        return [.. (await services.GetRequiredService<IMcpServerStore>().LoadAsync(ct).ConfigureAwait(false)).Where(server => IsEditable(server, internalNames))];
    }

    private static async Task<McpServerConfig?> _OAuthRowAsync(IServiceProvider services, string id, CancellationToken ct) =>
        (await _EditableAsync(services, ct).ConfigureAwait(false)).FirstOrDefault(server => server.Id == id && server.Transport == McpTransport.Http && server.Auth == McpServerAuth.OAuth);

    private static IResult _Invalid(string description) =>
        BackendApiRoutes.Error(StatusCodes.Status400BadRequest, "invalid_request", description);
}

internal sealed record McpServersBody([property: JsonRequired] IReadOnlyList<McpServerWire> Servers);

internal sealed record McpServerWire(
    [property: JsonRequired] string Id,
    [property: JsonRequired] string Name,
    [property: JsonRequired] McpTransport Transport,
    [property: JsonRequired] McpServerScope Scope,
    string? Command,
    IReadOnlyList<string>? Args,
    string? Url,
    [property: JsonRequired] McpServerAuth Auth,
    string? ApiKey,
    string? OAuthAuthority,
    string? OAuthClientId,
    string? OAuthScopes,
    IReadOnlyList<McpHeader>? Headers,
    [property: JsonRequired] bool Enabled)
{
    public override string ToString() => $"{nameof(McpServerWire)} {{ {nameof(Id)} = {Id}, {nameof(Name)} = {Name} }}";
}
