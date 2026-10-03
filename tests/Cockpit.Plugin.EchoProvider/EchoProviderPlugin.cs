using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.EchoProvider;

// AC-1444: an SDK provider that answers "echo: <prompt> (cli <pid>)". Each session runs a real child process, as a CLI
// would, and ends it when disposed; the pid in the answer lets a journey see that it is gone after the cockpit stopped.
public sealed class EchoProviderPlugin : ICockpitPlugin
{
    public const string ProviderId = "echo-provider.echo";

    public PluginMetadata Metadata { get; } = new(
        Id: "echo-provider",
        DisplayName: "Echo provider",
        Author: "Cockpit",
        Description: "Test fixture: an SDK session provider that answers with an echo.");

    public void ConfigureServices(IServiceCollection services)
    {
    }

    public void Initialize(ICockpitHost host)
    {
        host.AddSessionProvider(new SessionProviderRegistration(
            ProviderId,
            "Echo",
            _ => new EchoDriverFactory(),
            new PluginSessionCapabilities(SupportsTools: false, SupportsPermissions: false))
        {
            IsLoggedIn = _IsSignedIn,
        });
    }

    // AC-1357: a sign-in a journey can take away and give back — signed in while the file the config names exists.
    // A config without `signedInFile` is always signed in.
    private static bool _IsSignedIn(string configJson) =>
        JsonNode.Parse(configJson)?["signedInFile"]?.GetValue<string>() is not { } file || File.Exists(file);

    public void Dispose()
    {
    }
}

internal sealed class EchoDriverFactory : IPluginSessionDriverFactory
{
    public IPluginSessionDriver Create(string configJson) => new EchoSessionDriver();
}

internal sealed class EchoSessionDriver : IPluginSessionDriver
{
    private readonly PluginSessionEventPublisher _events = new();
    private Process? _child;

    public PluginSessionCapabilities Capabilities { get; } = new(SupportsTools: false, SupportsPermissions: false);

    public string? SessionId => "echo-conversation";

    public int? ProcessId => _child?.Id;

    public IAsyncEnumerable<PluginSessionEvent> Events => _events.Events;

    public Task StartAsync(string? model = null, CancellationToken cancellationToken = default)
    {
        // Its own pipes, not the cockpit's: a child holding those would keep a reader of the cockpit's output waiting.
        var start = OperatingSystem.IsWindows() ? new ProcessStartInfo("ping", "-n 600 127.0.0.1") : new ProcessStartInfo("sleep", "600");
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        _child = Process.Start(start);
        return Task.CompletedTask;
    }

    public Task SendUserMessageAsync(string text, CancellationToken cancellationToken = default)
    {
        var answer = $"echo: {text} (cli {_child?.Id})";
        _events.Publish(new PluginAssistantTextDelta { SessionId = SessionId, BlockIndex = 0, Text = answer });
        _events.Publish(new PluginTurnCompleted { SessionId = SessionId, Subtype = "success", Result = answer, IsError = false });
        return Task.CompletedTask;
    }

    public Task InterruptAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task RespondToPermissionAsync(string toolUseId, bool allow, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _events.TryComplete();
        if (_child is { HasExited: false })
        {
            _child.Kill(entireProcessTree: true);
        }

        _child?.Dispose();
        return ValueTask.CompletedTask;
    }
}
