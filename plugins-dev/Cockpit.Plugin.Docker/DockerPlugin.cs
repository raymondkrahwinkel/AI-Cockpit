using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.Plugin.Docker.Compose;
using Cockpit.Plugin.Docker.Contracts;
using Cockpit.Plugin.Docker.Engine;
using Cockpit.Plugin.Docker.Mcp;
using Cockpit.Plugin.Docker.Security;
using Cockpit.Plugin.Docker.Settings;
using Cockpit.Plugin.Docker.StatusBar;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Docker;

// Docker plugin entry point (AC-84). Registers the local Docker daemon and injects a cockpit-hosted
// `cockpit-docker` MCP server so an agent can work with containers under strict, human-approved control. Proxy
// model — the plugin talks to the Docker Engine API itself (via Docker.DotNet), keeps the connection, and gates
// every call through `DockerAccessGate`. Sibling of the Kubernetes plugin (AC-80).
public sealed class DockerPlugin : ICockpitPlugin
{
    public PluginMetadata Metadata { get; } = new(
        Id: "docker",
        DisplayName: "Docker",
        Author: "Cockpit",
        Description: "Register the local Docker daemon and give agents scoped, human-approved access to containers " +
            "through a cockpit-docker MCP server. The plugin talks to the Docker Engine API itself and keeps the " +
            "connection — an agent never gets the Docker socket. Every call is gated: the first touch of the daemon " +
            "asks for consent, and every change asks afresh with the literal command shown and is never remembered.");

    private DockerEngine? _engine;
    private readonly List<IDisposable> _handlers = [];

    public void ConfigureServices(IServiceCollection services)
    {
    }

    public void Initialize(ICockpitHost host)
    {
        var settings = new DockerSettings(host.Storage);
        var engine = new DockerEngine(settings);
        _engine = engine;
        var gate = new DockerAccessGate(host, settings);
        var compose = new ComposeCli();
        var docker = new DockerCli();
        var running = new RunningContainerRegistry(engine, () => DateTimeOffset.UtcNow);
        var tools = new DockerMcpTools(settings, gate, engine, compose, docker, running);

        _ = host.AddMcpEndpoint("cockpit-docker", tools, isEnabled: () => settings.McpEnabled);

        // Detached containers this plugin started show in the status bar with an operator-only Kill (AC-82).
        host.AddSupervisedActivityProvider(running);

        // AC-1394: the UI part's settings dialog lives in its own assembly now, so it tells us over the channel
        // when the operator saves — a settings save may have changed the daemon endpoint, so drop the cached
        // client and let the next call rebuild it.
        _handlers.Add(host.Channel.Handle(DockerChannel.SettingsSaved, (_, _) =>
        {
            engine.Invalidate();
            return Task.FromResult(JsonSerializer.SerializeToElement(true, DockerChannel.Json));
        }));
    }

    public void Dispose()
    {
        foreach (var handler in _handlers)
        {
            handler.Dispose();
        }

        _handlers.Clear();
        _engine?.Dispose();
    }
}
