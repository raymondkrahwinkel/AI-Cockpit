using System.Runtime.Loader;
using Avalonia.Controls;
using Avalonia.Threading;
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

        // AC-850: the Diagram/Whiteboard tabs and the diagrams-list tab are gone — nothing registers a workspace
        // type any more, only toolbar actions: one list opener per surface (AC-896 moved "Nieuw ..." into that
        // list's own header, next to Refresh).
        Assert.Empty(host.WorkspaceTypes);
        Assert.Equal(["Diagrams", "Whiteboards", "Wireframes"], host.ToolbarActions.Select(a => a.Title));

        // The measurement: nothing named "Avalonia*" ever loaded into the plugin's own AssemblyLoadContext —
        // everything the panel needed from the Avalonia family (including Svg.Controls.Skia.Avalonia's own
        // Avalonia.Skia dependency) came from the host's default context instead.
        var pluginAlc = AssemblyLoadContext.GetLoadContext(plugin.GetType().Assembly);
        Assert.NotNull(pluginAlc);
        Assert.DoesNotContain(pluginAlc!.Assemblies, a => a.GetName().Name?.StartsWith("Avalonia", StringComparison.Ordinal) == true);

        // AC-826/AC-850: "Diagrams" opens a dialog, not a workspace; its body builds against a host with no
        // linked project (default GetProjectMemoryRowsAsync).
        host.ToolbarActions[0].OnInvoke().GetAwaiter().GetResult();
        Assert.Empty(host.OpenedWorkspaceTypeIds);
        var listDialog = Assert.Single(host.Dialogs, d => d.Key == "diagram.list");
        Assert.IsAssignableFrom<Control>(listDialog.Content);

        // AC-816/AC-834/AC-896: "Nieuw diagram" now lives in that list's own header. Clicking it opens the
        // quick-start dialog; RecordingHost.ShowDialogAsync clicks straight through with the prefilled name, the
        // same "Enter is enough" default an operator gets.

        // The list body is a UserControl — its Content only materialises into the visual tree once templated,
        // which showing it in a window forces.
        var diagramListWindow = new Window { Content = listDialog.Content };
        diagramListWindow.Show();
        Dispatcher.UIThread.RunJobs();
        listDialog.Content.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Nieuw diagram"))
            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        diagramListWindow.Close();
        Assert.Empty(host.OpenedWorkspaceTypeIds);
        var diagramDialog = Assert.Single(host.Dialogs, d => d.Key.StartsWith("diagram.document.", StringComparison.Ordinal));

        // A non-null cast to the host's own Control is itself the identity proof the ticket asks for: a second
        // Avalonia.Base in the plugin's context would fail this with an InvalidCastException, not a false
        // assertion — the plugin's panel simply would not fit the host's visual tree.
        Assert.IsAssignableFrom<Control>(diagramDialog.Content);

        // AC-836/AC-842/AC-896: same two-stage path for the whiteboard surface — no IWhiteboardAccessRegistry in
        // this host's services, the "no host to fall through to" case the panel has to survive. The toolbar action
        // opens the list, "Nieuw whiteboard" opens a window bound to the active session.
        host.ToolbarActions[1].OnInvoke().GetAwaiter().GetResult();
        Assert.Empty(host.OpenedWorkspaceTypeIds);
        var whiteboardListDialog = Assert.Single(host.Dialogs, d => d.Key == "whiteboard.list");
        var whiteboardListWindow = new Window { Content = whiteboardListDialog.Content };
        whiteboardListWindow.Show();
        Dispatcher.UIThread.RunJobs();
        whiteboardListDialog.Content.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Nieuw whiteboard"))
            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        whiteboardListWindow.Close();
        var whiteboardDialog = Assert.Single(host.Dialogs, d => d.Key.StartsWith("whiteboard.document.", StringComparison.Ordinal));
        Assert.IsAssignableFrom<Control>(whiteboardDialog.Content);

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

        public List<(string Key, Control Content)> Dialogs { get; } = [];

        public IServiceProvider Services { get; } = new ServiceCollection().BuildServiceProvider();

        public ICockpitActions Actions { get; } = new NoActions();

        public IPluginStorage Storage { get; } = new MemoryStorage();

        public ICockpitSessionObserver Sessions { get; } = new FakeSessions();

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

        public Task ShowDialogAsync(string title, Func<Control> createContent, double width = 720, double height = 560) =>
            ShowDialogAsync(title, createContent, singleInstanceKey: "", width, height);

        // Builds the quick-start dialog's content and clicks its "Openen" button straight away, standing in for
        // an operator who typed nothing and hit Enter — the prefilled name is already a working default. Every
        // other dialog (the diagram/whiteboard windows, the diagrams list) is only recorded, unclicked.
        public Task ShowDialogAsync(string title, Func<Control> createContent, string singleInstanceKey, double width = 720, double height = 560)
        {
            var content = createContent();
            Dialogs.Add((singleInstanceKey, content));
            if (singleInstanceKey is "diagram.quickstart" or "whiteboard.quickstart")
            {
                content.GetVisualDescendants().OfType<Button>().First(b => Equals(b.Content, "Openen"))
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
