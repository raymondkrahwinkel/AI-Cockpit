using System.Text.Json.Nodes;
using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Cockpit.App.Plugins;
using Cockpit.Core.Abstractions.Whiteboard;
using Cockpit.Core.Plugins;
using Cockpit.Infrastructure.Consent;
using Cockpit.Infrastructure.Whiteboard;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Consent;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.Workspaces;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-829: opens the real whiteboard plugin's panel (AC-822) through the actual PluginActivator/PluginLoadContext,
/// then reads it back through the real WhiteboardMcpTools (AC-823) over the same registry instance — the
/// producer/consumer gap the ticket closes, neither side stubbed out.
/// </summary>
[Collection("avalonia")]
public class WhiteboardAccessIntegrationTests
{
    [Fact]
    public async Task OpeningThePanel_RegistersARealSnapshot_ThatReadWhiteboardReturnsUnchanged() => await HeadlessAvalonia.RunAsync(async () =>
    {
        var folder = _LocatePluginOutput();
        Assert.NotNull(folder);

        var manifestJson = File.ReadAllText(Path.Combine(folder!, "plugin.json"));
        Assert.True(PluginManifest.TryParse(manifestJson, out var manifest, out _));
        Assert.NotNull(manifest);

        var hash = PluginHash.Compute(File.ReadAllBytes(Path.Combine(folder, manifest!.EntryAssembly)));
        var discovered = new DiscoveredPlugin(folder, "diagram", manifest, hash, PluginLoadDecision.Load);

        var activator = new PluginActivator(NullLogger<PluginActivator>.Instance);
        var plugin = activator.Activate(discovered);
        Assert.NotNull(plugin);
        plugin!.ConfigureServices(new ServiceCollection());

        var registry = new WhiteboardAccessRegistry();
        var host = new RecordingHost(registry);
        plugin.Initialize(host);

        // AC-836: the whiteboard is one of the merged plugin's surfaces now, so it is picked by id rather than
        // being the only registration.
        var registration = Assert.Single(host.WorkspaceTypes, type => type.Id == "whiteboard.panel");
        var body = registration.CreateBody(new FakeWorkspaceContext());
        Assert.IsAssignableFrom<Control>(body);

        // The panel signed up with a real rendered snapshot, not a hand-fed byte array — PeekSnapshot is the
        // operator-trusted read the consent prompt and ReadWhiteboard both build from.
        var peeked = registry.PeekSnapshot("test-workspace");
        Assert.NotNull(peeked);
        Assert.NotEmpty(peeked!);

        var tools = new WhiteboardMcpTools(registry, new AlwaysApprove());
        var json = JsonNode.Parse(await tools.ReadWhiteboard("agent-pane", "test-workspace"));

        Assert.True(json!["ok"]!.GetValue<bool>());
        Assert.Equal(Convert.ToBase64String(peeked!), json["imageBase64"]!.GetValue<string>());

        plugin.Dispose();
    });

    // Walks up from the test output to the repo root and finds the plugin's build output (either config).
    private static string? _LocatePluginOutput()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidateRoot = Path.Combine(directory.FullName, "plugins-dev", "Cockpit.Plugin.Diagram", "bin");
            if (Directory.Exists(candidateRoot))
            {
                var dll = Directory
                    .EnumerateFiles(candidateRoot, "Cockpit.Plugin.Diagram.dll", SearchOption.AllDirectories)
                    .FirstOrDefault();
                return dll is null ? null : Path.GetDirectoryName(dll);
            }

            directory = directory.Parent;
        }

        return null;
    }

    private sealed class AlwaysApprove : IConsentBroker
    {
        public Task<ConsentDecision> RequestConsentAsync(ConsentRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ConsentDecision(ConsentOutcome.Approved));

        public event EventHandler<ConsentPrompt>? PromptOpened { add { } remove { } }

        public event EventHandler<Guid>? PromptClosed { add { } remove { } }

        public void Respond(Guid promptId, ConsentOutcome outcome, bool remember)
        {
        }
    }

    private sealed class RecordingHost(IWhiteboardAccessRegistry registry) : ICockpitHost
    {
        public List<WorkspaceTypeRegistration> WorkspaceTypes { get; } = [];

        public IServiceProvider Services { get; } = new ServiceCollection()
            .AddSingleton(registry)
            .BuildServiceProvider();

        public ICockpitActions Actions { get; } = new NoActions();

        public IPluginStorage Storage { get; } = new MemoryStorage();

        public void AddSettings(Func<Control> createView)
        {
        }

        public void AddSideMenuButton(string title, Action onInvoke)
        {
        }

        public void AddSideMenuSection(string title, Func<Control> createView)
        {
        }

        public void AddWorkspaceType(WorkspaceTypeRegistration registration) => WorkspaceTypes.Add(registration);

        public void AddToolbarAction(ToolbarAction action)
        {
        }

        public Task OpenWorkspaceAsync(string workspaceTypeId) => Task.CompletedTask;

        public Task ShowDialogAsync(string title, Func<Control> createContent, double width = 720, double height = 560) => Task.CompletedTask;
    }

    private sealed class NoActions : ICockpitActions
    {
        public bool HasActiveSession => false;

        public Task InjectIntoActiveSessionAsync(string text) => Task.CompletedTask;

        public Task SetClipboardTextAsync(string text) => Task.CompletedTask;
    }

    private sealed class MemoryStorage : IPluginStorage
    {
        private readonly Dictionary<string, object?> _values = [];

        public T? Get<T>(string key) => _values.TryGetValue(key, out var value) ? (T?)value : default;

        public void Set<T>(string key, T value) => _values[key] = value;
    }

    private sealed class FakeWorkspaceContext : IWorkspaceContext
    {
        public string WorkspaceId => "test-workspace";

        public IPluginStorage Storage { get; } = new MemoryStorage();

        public ICockpitSessionObserver Sessions => NullCockpitSessionObserver.Instance;

        // The whiteboard workspace body never embeds a session (unlike Diagram's, AC-824), so this is never
        // actually called by CreateBody — implemented only to satisfy the interface.
        public IEmbeddedSession EmbedSession(EmbeddedSessionRequest request) => throw new NotSupportedException();

        public event EventHandler? RefreshRequested
        {
            add { }
            remove { }
        }
    }
}
