using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Docker.Settings;

// Plugin settings, read fresh from `IPluginStorage` on every access so a settings save takes effect
// without a restart. Non-secret values (endpoint, capability flags) go through `Get/Set`; any future daemon TLS
// material would go through `SetSecret/GetSecret` so the host can encrypt it at rest.
internal sealed class DockerSettings(IPluginStorage storage)
{
    // Whether the cockpit-docker MCP server is offered to sessions. On by default.
    public bool McpEnabled
    {
        get => storage.Get<bool?>("mcpEnabled") ?? true;
        set => storage.Set("mcpEnabled", value);
    }

    // Whether exec/run into containers is allowed at all. Off by default (a dangerous capability).
    public bool AllowExec
    {
        get => storage.Get<bool?>("allowExec") ?? false;
        set => storage.Set("allowExec", value);
    }

    // The Docker daemon endpoint. Blank = the local default socket (npipe on Windows, unix socket elsewhere).
    public string DaemonEndpoint
    {
        get => storage.Get<string>("daemonEndpoint") ?? string.Empty;
        set => storage.Set("daemonEndpoint", value);
    }

    // AC-1348: stored together with the endpoint it was chosen for. A DaemonEndpoint that has since moved on
    // (compared normalized — trimmed) reads back as AlwaysAsk rather than the stored mode.
    public DockerConsentMode ConsentMode
    {
        get => storage.Get<string>("consentModeEndpoint") == _NormalizedEndpoint
            ? storage.Get<DockerConsentMode?>("consentMode") ?? DockerConsentMode.AlwaysAsk
            : DockerConsentMode.AlwaysAsk;
        set
        {
            storage.Set("consentMode", value);
            storage.Set("consentModeEndpoint", _NormalizedEndpoint);
        }
    }

    private string _NormalizedEndpoint => DaemonEndpoint.Trim();
}
