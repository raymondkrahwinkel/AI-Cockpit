using System.Runtime.Loader;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Cockpit.App.Plugins;
using Cockpit.Core.Plugins;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.Workspaces;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-809's ALC measurement: loads the real, compiled Diagram plugin through the actual
/// <see cref="PluginActivator"/>/<see cref="PluginLoadContext"/> and checks that nothing Avalonia-family
/// loaded into the plugin's own context. No pixel sampling — Avalonia's headless renderer doesn't reliably
/// composite this control's custom draw op; the real render was verified in a running dev cockpit (AC-809).
/// </summary>
[Collection("avalonia")]
public class DiagramPluginLoadTests
{
    [Fact]
    public void ActivatesAndContributes_WithNoAvaloniaFamilyAssemblyInThePluginsOwnContext() => HeadlessAvalonia.Run(() =>
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

        var host = new RecordingHost();
        plugin.Initialize(host);

        // AC-836: one plugin, both surfaces — the whiteboard keeps its own workspace type id, so a saved
        // workspace that held "whiteboard.panel" still finds it after the merge.
        Assert.Equal(["diagram.panel", "diagram.list", "whiteboard.panel"], host.WorkspaceTypes.Select(r => r.Id));
        var registration = host.WorkspaceTypes[0];
        var toolbarAction = host.ToolbarActions[0];

        // The measurement: nothing named "Avalonia*" ever loaded into the plugin's own AssemblyLoadContext —
        // everything the panel needed from the Avalonia family (including Svg.Controls.Skia.Avalonia's own
        // Avalonia.Skia dependency) came from the host's default context instead.
        var pluginAlc = AssemblyLoadContext.GetLoadContext(plugin.GetType().Assembly);
        Assert.NotNull(pluginAlc);
        Assert.DoesNotContain(pluginAlc!.Assemblies, a => a.GetName().Name?.StartsWith("Avalonia", StringComparison.Ordinal) == true);

        var body = registration.CreateBody(new FakeWorkspaceContext());

        // A non-null cast to the host's own Control is itself the identity proof the ticket asks for: a second
        // Avalonia.Base in the plugin's context would fail this with an InvalidCastException, not a false
        // assertion — the plugin's panel simply would not fit the host's visual tree.
        Assert.IsAssignableFrom<Control>(body);

        // AC-826: the list body builds too, against a host with no linked project (default GetProjectMemoryRowsAsync).
        var listBody = host.WorkspaceTypes[1].CreateBody(new FakeWorkspaceContext());
        Assert.IsAssignableFrom<Control>(listBody);

        // AC-816: the quick-start button opens a dialog first; RecordingHost.ShowDialogAsync builds it and clicks
        // straight through with the prefilled name, the same "Enter is enough" default an operator gets. AC-834:
        // what it opens after that is a window of its own, not a workspace tab.
        toolbarAction.OnInvoke().GetAwaiter().GetResult();
        Assert.Empty(host.OpenedWorkspaceTypeIds);
        Assert.Single(host.DialogKeys, key => key.StartsWith("diagram.document.", StringComparison.Ordinal));

        // AC-836: the whiteboard surface builds from the same ALC — with no IWhiteboardAccessRegistry in this
        // host's services, which is the "no host to fall through to" case the panel has to survive.
        var whiteboardBody = host.WorkspaceTypes[2].CreateBody(new FakeWorkspaceContext());
        Assert.IsAssignableFrom<Control>(whiteboardBody);

        host.ToolbarActions[2].OnInvoke().GetAwaiter().GetResult();
        Assert.Equal(["whiteboard.panel"], host.OpenedWorkspaceTypeIds);

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

    private sealed class RecordingHost : ICockpitHost
    {
        public List<WorkspaceTypeRegistration> WorkspaceTypes { get; } = [];

        public List<ToolbarAction> ToolbarActions { get; } = [];

        public List<string> OpenedWorkspaceTypeIds { get; } = [];

        public IServiceProvider Services { get; } = new ServiceCollection().BuildServiceProvider();

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

        public void AddToolbarAction(ToolbarAction action) => ToolbarActions.Add(action);

        public Task OpenWorkspaceAsync(string workspaceTypeId)
        {
            OpenedWorkspaceTypeIds.Add(workspaceTypeId);
            return Task.CompletedTask;
        }

        public List<string> DialogKeys { get; } = [];

        public Task ShowDialogAsync(string title, Func<Control> createContent, double width = 720, double height = 560) =>
            ShowDialogAsync(title, createContent, singleInstanceKey: "", width, height);

        // Builds the quick-start dialog's content and clicks its "Openen" button straight away, standing in for
        // an operator who typed nothing and hit Enter — the prefilled name is already a working default. The
        // diagram window that follows is only recorded; its own body is what DiagramCollabWindowTests exercises.
        public Task ShowDialogAsync(string title, Func<Control> createContent, string singleInstanceKey, double width = 720, double height = 560)
        {
            DialogKeys.Add(singleInstanceKey);
            var content = createContent();
            if (singleInstanceKey == "diagram.quickstart")
            {
                content.GetVisualDescendants().OfType<Button>().First(b => Equals(b.Content, "Openen"))
                    .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            }

            return Task.CompletedTask;
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

    private sealed class FakeWorkspaceContext : IWorkspaceContext
    {
        public string WorkspaceId => "test-workspace";

        public IPluginStorage Storage { get; } = new MemoryStorage();

        public ICockpitSessionObserver Sessions => NullCockpitSessionObserver.Instance;

        // AC-824: the body now embeds a session as its own surface, so this needs a placeable stand-in
        // rather than a throw — same shape as Cockpit.Plugin.FanOut.Tests' FakeEmbeddedSession.
        public IEmbeddedSession EmbedSession(EmbeddedSessionRequest request) => new FakeEmbeddedSession();

        public event EventHandler? RefreshRequested
        {
            add { }
            remove { }
        }
    }

    private sealed class FakeEmbeddedSession : IEmbeddedSession
    {
        public Control View { get; } = new Border();

        public string PaneId => "pane-fake";

        public Task CloseAsync() => Task.CompletedTask;

        public void SetInputEnabled(bool enabled)
        {
        }

        public Task<string?> Completion { get; } = Task.FromResult<string?>(null);
    }
}
