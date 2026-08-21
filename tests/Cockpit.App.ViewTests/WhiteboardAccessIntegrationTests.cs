using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Cockpit.App.Plugins;
using Cockpit.Core.Abstractions.Whiteboard;
using Cockpit.Core.Plugins;
using Cockpit.Infrastructure.Whiteboard;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Consent;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.Workspaces;
using ModelContextProtocol.Protocol;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-829: opens the real whiteboard plugin's panel (AC-822) through the actual PluginActivator/PluginLoadContext,
/// then reads it back through the real WhiteboardMcpTools (AC-823, moved into the plugin by AC-890) over the same
/// registry instance — the producer/consumer gap the ticket closes, neither side stubbed out. AC-890's deviation:
/// WhiteboardMcpTools is internal to the plugin assembly now, which this project only references as a build
/// dependency (ReferenceOutputAssembly=false, same as DiagramPluginLoadTests), so the instance DiagramPlugin.Initialize
/// actually wires up is captured off RecordingHost.AddMcpEndpoint and driven through reflection.
/// </summary>
[Collection("avalonia")]
public class WhiteboardAccessIntegrationTests
{
    [Fact]
    public async Task OpeningThePanel_RegistersARealSnapshot_ThatReadWhiteboardReturnsUnchanged() => await HeadlessAvalonia.RunAsync(async () =>
    {
        var (plugin, registry, tools, surfaceId) = await _OpenBoardAsync();

        // The panel signed up with a real rendered snapshot, not a hand-fed byte array — PeekSnapshot is the
        // operator-trusted read the consent prompt and ReadWhiteboard both build from.
        var peeked = registry.PeekSnapshot(surfaceId);
        Assert.NotNull(peeked);
        Assert.NotEmpty(peeked!);

        var result = await _CallReadWhiteboardAsync(tools, "agent-pane", surfaceId);
        var json = JsonNode.Parse(Assert.IsType<TextContentBlock>(result.Content[0]).Text);

        Assert.True(json!["ok"]!.GetValue<bool>());
        var image = Assert.IsType<ImageContentBlock>(result.Content[1]);
        Assert.Equal(peeked, image.DecodedData.ToArray());

        plugin.Dispose();
    });

    [Fact]
    public async Task PlacingAnObject_ReachesTheRealBoard_AndTheAgentCanOnlyTakeBackItsOwn() => await HeadlessAvalonia.RunAsync(async () =>
    {
        // AC-854 end to end: the write path is only real if what the agent places actually lands on the operator's
        // board and comes back in the snapshot it reads.
        var (plugin, registry, tools, surfaceId) = await _OpenBoardAsync();
        var empty = registry.PeekSnapshot(surfaceId)!;

        var placed = JsonNode.Parse(await _CallAsync(tools, "PlaceOnWhiteboard", "agent-pane", surfaceId, "stickynote", "Van de agent", 100d, 100d, 0d, 0d));
        Assert.True(placed!["ok"]!.GetValue<bool>());
        Dispatcher.UIThread.RunJobs();

        var withNote = registry.PeekSnapshot(surfaceId)!;
        Assert.NotEqual(empty, withNote);

        var strange = JsonNode.Parse(await _CallAsync(tools, "EraseWhiteboardObject", "agent-pane", surfaceId, Guid.NewGuid().ToString()));
        Assert.False(strange!["ok"]!.GetValue<bool>());
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(withNote, registry.PeekSnapshot(surfaceId));

        var erased = JsonNode.Parse(await _CallAsync(tools, "EraseWhiteboardObject", "agent-pane", surfaceId, placed["objectId"]!.GetValue<string>()));
        Assert.True(erased!["ok"]!.GetValue<bool>());
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(empty, registry.PeekSnapshot(surfaceId));

        plugin.Dispose();
    });

    // WhiteboardMcpTools is internal to the plugin assembly (AC-890); this project only build-references that
    // assembly (see the class comment), so its methods are reached the way the real MCP host reaches them too —
    // dynamically, by name — rather than through a compile-time type this project cannot see.
    private static Task<string> _CallAsync(object tools, string method, params object?[] args) =>
        (Task<string>)tools.GetType().GetMethod(method)!.Invoke(tools, args)!;

    // AC-1007: read_whiteboard alone returns CallToolResult (its image travels as its own content block) —
    // reflected separately since _CallAsync's cast is fixed to the other tools' plain-string replies.
    private static Task<CallToolResult> _CallReadWhiteboardAsync(object tools, params object?[] args) =>
        (Task<CallToolResult>)tools.GetType().GetMethod("ReadWhiteboard")!.Invoke(tools, args)!;

    private static async Task<(ICockpitPlugin Plugin, WhiteboardAccessRegistry Registry, object Tools, string SurfaceId)> _OpenBoardAsync()
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

        // AC-850/AC-896: the whiteboard is no longer a workspace type — the "Whiteboards" toolbar action opens the
        // list dialog, whose header's "New whiteboard" button opens the board as a window bound to the active
        // session, through W-2/AC-843's snelstart.
        var whiteboardAction = Assert.Single(host.ToolbarActions, action => action.Title == "Whiteboards");
        await whiteboardAction.OnInvoke();
        Assert.Contains("whiteboard.list", host.DialogKeys);
        var listContent = host.LastDialogContent!;

        // The list body is a UserControl — its Content only materialises into the visual tree once templated,
        // which showing it in a window forces.
        var listWindow = new Window { Content = listContent };
        listWindow.Show();
        Dispatcher.UIThread.RunJobs();
        listContent.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "New whiteboard"))
            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        listWindow.Close();
        var dialogKey = Assert.Single(host.DialogKeys, key => key.StartsWith("whiteboard.document.", StringComparison.Ordinal));
        Assert.IsAssignableFrom<Control>(host.LastDialogContent);

        Assert.True(host.McpTools.TryGetValue("cockpit-whiteboard", out var tools));
        return (plugin, registry, tools!, dialogKey["whiteboard.document.".Length..]);
    }

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

    private sealed class RecordingHost(IWhiteboardAccessRegistry registry) : ICockpitHost
    {
        public List<ToolbarAction> ToolbarActions { get; } = [];

        public List<string> DialogKeys { get; } = [];

        public Control? LastDialogContent { get; private set; }

        // AC-890: what DiagramPlugin.Initialize actually mounted, keyed by server name — the real WhiteboardMcpTools
        // instance, wired to this host and to the registry above, captured the same way the real MCP host would see it.
        public Dictionary<string, object> McpTools { get; } = [];

        public IServiceProvider Services { get; } = new ServiceCollection()
            .AddSingleton(registry)
            .BuildServiceProvider();

        public ICockpitActions Actions { get; } = new NoActions();

        public IPluginStorage Storage { get; } = new MemoryStorage();

        public ICockpitSessionObserver Sessions { get; } = new FakeSessions();

        public Task AddMcpEndpoint(string serverName, object tools, Func<bool>? isEnabled, bool isInternal)
        {
            McpTools[serverName] = tools;
            return Task.CompletedTask;
        }

        public Task<ConsentDecision> RequestConsentAsync(ConsentRequest request) =>
            Task.FromResult(new ConsentDecision(ConsentOutcome.Approved));

        public void AddSettings(Func<Control> createView)
        {
        }

        public void AddSideMenuButton(string title, Action onInvoke)
        {
        }

        public void AddSideMenuSection(string title, Func<Control> createView)
        {
        }

        public void AddWorkspaceType(WorkspaceTypeRegistration registration)
        {
        }

        public void AddToolbarAction(ToolbarAction action) => ToolbarActions.Add(action);

        public Task OpenWorkspaceAsync(string workspaceTypeId) => Task.CompletedTask;

        public Task ShowDialogAsync(string title, Func<Control> createContent, double width = 720, double height = 560) =>
            ShowDialogAsync(title, createContent, singleInstanceKey: "", width, height);

        public Task ShowDialogAsync(string title, Func<Control> createContent, string singleInstanceKey, double width = 720, double height = 560)
        {
            DialogKeys.Add(singleInstanceKey);
            LastDialogContent = createContent();

            // W-2/AC-843 put a snelstart in front of the board: keep the prefilled name and hit Openen, the way an
            // operator would, so the board behind it actually opens.
            if (singleInstanceKey == "whiteboard.quickstart")
            {
                LastDialogContent.GetVisualDescendants().OfType<Button>().First(button => Equals(button.Content, "Open"))
                    .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            }

            return Task.CompletedTask;
        }
    }

    private sealed class FakeSessions : ICockpitSessionObserver
    {
        public string? ActiveSessionWorkingDirectory => null;

        public string? ActivePaneId => "pane-a";

        public event EventHandler? ActiveSessionChanged
        {
            add { }
            remove { }
        }

        public event EventHandler<SessionOutputText>? OutputProduced
        {
            add { }
            remove { }
        }
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
}
