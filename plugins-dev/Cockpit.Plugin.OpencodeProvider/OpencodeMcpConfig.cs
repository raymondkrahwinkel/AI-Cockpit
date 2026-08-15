using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.OpencodeProvider;

// Builds the ACP `mcpServers` array for `session/new`/`session/resume` from the host's shared MCP registry
// (AC-783) — in its own file so the stdio-vs-http shape is testable without spinning up a driver.
//
// Measured live in this session (not assumed from Kimi's own finding): a stdio-shaped server object with no
// `type` field — `{"name":"probe-stdio","command":"echo","args":["hi"],"env":[]}` — was accepted by a real
// `opencode acp` process and logged as `type=local` in its own debug output (opencode's internal name for a
// stdio server), then reported "server unavailable" only because `echo` is not a real persistent MCP server
// — the shape itself was accepted, not rejected. So the same two-shape design
// `Cockpit.Plugin.KimiProvider.KimiMcpConfig` uses carries over unchanged: a stdio server's wire object
// carries no `type` field at all (the "bare branch" of the union), and an http server carries `"type":"http"`.
// The initialize handshake's own `agentCapabilities.mcpCapabilities` only advertises `{http:true,sse:true}` —
// no `stdio` flag — which on its own would suggest stdio is unsupported; the live test above disproves that
// reading, so `mcpCapabilities` evidently only enumerates the two *remote* transport kinds the agent can
// additionally reach, not the always-available local/stdio default. Measuring instead of trusting that one
// field's absence is exactly what AC-783 asks for.
//
// Assumption, unmeasured (same one Kimi's own file makes): `PluginMcpServer` carries no signal that a URL
// server is SSE rather than plain HTTP (both ride `PluginMcpServer.Url`), so every URL server maps to
// `"type":"http"` here — there is no data on the plugin contract to ever pick `"sse"` from, even though
// opencode's own capabilities say it understands that transport too.
//
// Assumption: a cockpit-hosted server's bearer key has no env-var-reference mechanism on this wire (unlike
// Codex's `bearer_token_env_var` or Claude's own `${VAR}`-expanding config file) — ACP's `headers` is a
// literal array of `{name,value}`, so the header carries the resolved key value itself, read from the same
// environment the driver builds for the spawn. This does not repeat the world-readable-file risk the Claude
// plugin guards against: nothing here is written to disk, only sent once over the already-private stdio pipe
// to the child process.
internal static class OpencodeMcpConfig
{
    public static IReadOnlyList<object> Build(IReadOnlyList<PluginMcpServer>? servers, IReadOnlyDictionary<string, string?> environmentVariables)
    {
        if (servers is not { Count: > 0 })
        {
            return [];
        }

        var wire = new List<object>(servers.Count);
        foreach (var server in servers)
        {
            if (_BuildStdio(server) is { } stdio)
            {
                wire.Add(stdio);
            }
            else if (_BuildHttp(server, environmentVariables) is { } http)
            {
                wire.Add(http);
            }
        }

        return wire;
    }

    private static object? _BuildStdio(PluginMcpServer server)
    {
        if (string.IsNullOrWhiteSpace(server.Command))
        {
            return null;
        }

        // No "type" property at all — see the remarks above.
        return new
        {
            name = server.Name,
            command = server.Command,
            args = server.Args,
            env = Array.Empty<object>(),
        };
    }

    private static object? _BuildHttp(PluginMcpServer server, IReadOnlyDictionary<string, string?> environmentVariables)
    {
        if (string.IsNullOrWhiteSpace(server.Url))
        {
            return null;
        }

        return new
        {
            type = "http",
            name = server.Name,
            url = server.Url,
            headers = _BuildHeaders(server, environmentVariables),
        };
    }

    private static object[] _BuildHeaders(PluginMcpServer server, IReadOnlyDictionary<string, string?> environmentVariables)
    {
        var token = server.CockpitHosted
            ? environmentVariables.GetValueOrDefault(WellKnownSessionEnvironment.CockpitMcpKey)
            : server.BearerToken;

        // The operator's own headers for a server that wants X-Api-Key or another scheme, then the bearer the
        // auth setting produced. The host has already removed Authorization from that set when a credential
        // covers it, so the two cannot both be sent.
        var headers = new List<object>();
        foreach (var (name, value) in server.Headers)
        {
            headers.Add(new { name, value });
        }

        if (!string.IsNullOrEmpty(token))
        {
            headers.Add(new { name = "Authorization", value = $"Bearer {token}" });
        }

        return [.. headers];
    }
}
