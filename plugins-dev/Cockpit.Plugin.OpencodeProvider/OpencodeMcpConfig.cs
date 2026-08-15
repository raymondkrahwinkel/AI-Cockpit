using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.OpencodeProvider;

// AC-783: builds the ACP `mcpServers` array. Measured live (a stdio server with no `type` field was accepted
// by a real opencode acp process): the same two-shape design KimiMcpConfig uses carries over unchanged — a
// stdio server carries no `type` field, an http server carries `"type":"http"`.
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
