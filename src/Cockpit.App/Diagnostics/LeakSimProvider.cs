#if DEBUG
using System.Threading.Channels;
using Avalonia.Controls;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.App.Diagnostics;

// Dev-only fake session provider for the leak simulation (CockpitViewModel.RunLeakSimAsync). It drives scripted
// agent events through the REAL session pipeline — StartConfiguredAsync → SessionRuntime → SessionViewModel.Apply
// — so the sim exercises the same runtime, activity timers and Focus "steps run" folding a real Claude session
// does, instead of the design-ctor stand-in that could not reproduce the after-close retention.
internal static class LeakSimProvider
{
    public const string ProviderId = "leaksim";

    // The driver the factory last minted, so the sim can push events into the live session it just started.
    public static LeakSimDriver? Current { get; private set; }

    // Registers the provider into the shared registry; a repeat call is a harmless replace.
    public static void EnsureRegistered(IPluginProviderRegistry registry) =>
        registry.Register(new SessionProviderRegistration(
            ProviderId,
            "Leak Sim",
            _ => new LeakSimDriverFactory(),
            new PluginSessionCapabilities(SupportsTools: true, SupportsPermissions: true),
            _ => new _ConfigView()));

    private sealed class LeakSimDriverFactory : IPluginSessionDriverFactory
    {
        public IPluginSessionDriver Create(string configJson) => Current = new LeakSimDriver();
    }

    private sealed class _ConfigView : IPluginProviderConfigView
    {
        public Control View { get; } = new TextBlock { Text = "leak sim" };

        public bool TryGetConfigJson(out string configJson)
        {
            configJson = "{}";
            return true;
        }
    }
}

// Channel-backed scripted driver (same shape as tests' FakePluginSessionDriver) — only the interface's required
// members; every other member keeps its default. The sim calls Emit to stream events and Complete to end.
internal sealed class LeakSimDriver : IPluginSessionDriver
{
    private const string Id = "leaksim-1";
    private readonly Channel<PluginSessionEvent> _events = Channel.CreateUnbounded<PluginSessionEvent>();

    public PluginSessionCapabilities Capabilities { get; } = new(SupportsTools: true, SupportsPermissions: true);

    public string? SessionId => Id;

    public IAsyncEnumerable<PluginSessionEvent> Events => _events.Reader.ReadAllAsync();

    public Task StartAsync(string? model = null, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task SendUserMessageAsync(string text, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task InterruptAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task RespondToPermissionAsync(string toolUseId, bool allow, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public void Emit(PluginSessionEvent pluginEvent) => _events.Writer.TryWrite(pluginEvent);

    public void Complete() => _events.Writer.TryComplete();

    public ValueTask DisposeAsync()
    {
        _events.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
#endif
