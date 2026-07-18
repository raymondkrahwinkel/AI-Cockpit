using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Docker.Settings;

/// <summary>
/// Plugin settings, read fresh from <see cref="IPluginStorage"/> on every access so a settings save takes effect
/// without a restart. Non-secret values (endpoint, capability flags) go through <c>Get/Set</c>; any future daemon TLS
/// material would go through <c>SetSecret/GetSecret</c> so the host can encrypt it at rest.
/// </summary>
internal sealed class DockerSettings(IPluginStorage storage)
{
    /// <summary>Whether the cockpit-docker MCP server is offered to sessions. On by default.</summary>
    public bool McpEnabled
    {
        get => storage.Get<bool?>("mcpEnabled") ?? true;
        set => storage.Set("mcpEnabled", value);
    }

    /// <summary>Whether exec/run into containers is allowed at all. Off by default (a dangerous capability).</summary>
    public bool AllowExec
    {
        get => storage.Get<bool?>("allowExec") ?? false;
        set => storage.Set("allowExec", value);
    }

    /// <summary>The Docker daemon endpoint. Blank = the local default socket (npipe on Windows, unix socket elsewhere).</summary>
    public string DaemonEndpoint
    {
        get => storage.Get<string>("daemonEndpoint") ?? string.Empty;
        set => storage.Set("daemonEndpoint", value);
    }
}
