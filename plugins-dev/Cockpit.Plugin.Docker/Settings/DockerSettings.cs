using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Docker.Settings;

// Plugin settings are read fresh so saved capability and consent changes take effect without a restart.
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

    // Keep the legacy endpoint binding so remote approvals never transfer to the local daemon.
    public DockerConsentMode ConsentMode
    {
        get => storage.Get<string>("consentModeEndpoint") == string.Empty
            ? storage.Get<DockerConsentMode?>("consentMode") ?? DockerConsentMode.AlwaysAsk
            : DockerConsentMode.AlwaysAsk;
        set
        {
            storage.Set("consentMode", value);
            storage.Set("consentModeEndpoint", string.Empty);
        }
    }
}
