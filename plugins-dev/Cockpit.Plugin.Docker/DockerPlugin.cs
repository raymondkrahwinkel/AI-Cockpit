using Material.Icons;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugin.Docker.Compose;
using Cockpit.Plugin.Docker.Engine;
using Cockpit.Plugin.Docker.Mcp;
using Cockpit.Plugin.Docker.Security;
using Cockpit.Plugin.Docker.Settings;
using Cockpit.Plugin.Docker.StatusBar;
using Cockpit.Plugin.Docker.Ui;

namespace Cockpit.Plugin.Docker;

/// <summary>
/// Docker plugin entry point (AC-84). Registers the local Docker daemon and injects a cockpit-hosted
/// <c>cockpit-docker</c> MCP server so an agent can work with containers under strict, human-approved control. Proxy
/// model — the plugin talks to the Docker Engine API itself (via Docker.DotNet), keeps the connection, and gates
/// every call through <see cref="DockerAccessGate"/>. Sibling of the Kubernetes plugin (AC-80).
/// </summary>
public sealed class DockerPlugin : ICockpitPlugin
{
    public PluginMetadata Metadata { get; } = new(
        Id: "docker",
        DisplayName: "Docker",
        Version: "0.1.0",
        Author: "Cockpit",
        Description: "Register the local Docker daemon and give agents scoped, human-approved access to containers " +
            "through a cockpit-docker MCP server. The plugin talks to the Docker Engine API itself and keeps the " +
            "connection — an agent never gets the Docker socket. Every call is gated: the first touch of the daemon " +
            "asks for consent, and every change asks afresh with the literal command shown and is never remembered.");

    private DockerEngine? _engine;

    public void ConfigureServices(IServiceCollection services)
    {
    }

    public void Initialize(ICockpitHost host)
    {
        var settings = new DockerSettings(host.Storage);
        var engine = new DockerEngine(settings);
        _engine = engine;
        var gate = new DockerAccessGate(host);
        var compose = new ComposeCli();
        var running = new RunningContainerRegistry(engine, () => DateTimeOffset.UtcNow);
        var tools = new DockerMcpTools(settings, gate, engine, compose, running);

        host.AddSettings(() => new DockerSettingsControl(settings));
        host.AddToolbarAction(new ToolbarAction("Docker settings", MaterialIconKind.Docker, () => host.ShowSettingsAsync()));
        _ = host.AddMcpEndpoint("cockpit-docker", tools, isEnabled: () => settings.McpEnabled);

        // Detached containers this plugin started show in the status bar with an operator-only Kill (AC-82).
        host.AddSupervisedActivityProvider(running);

        // A settings save may have changed the daemon endpoint; drop the cached client so the next call rebuilds.
        host.OnSettingsSaved(engine.Invalidate);
    }

    public void Dispose() => _engine?.Dispose();
}
