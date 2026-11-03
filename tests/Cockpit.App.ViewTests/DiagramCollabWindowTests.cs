using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Cockpit.Core.Abstractions.Diagrams;
using Cockpit.Core.Plugins;
using Cockpit.Infrastructure.Diagrams;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Plugins.Abstractions.Channels;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-834's acceptance, driven through the real compiled Diagram plugin: a diagram opens as its own window bound to
/// a session that is already running, one window per document, and nothing here starts or ends a session.
/// </summary>
[Collection("avalonia")]
public class DiagramCollabWindowTests
{
    [Fact]
    public Task ClosingTheWindow_LetsItsSessionKeepRunning() => HeadlessAvalonia.RunAsync(async () =>
    {
        var (plugin, host) = _StartPlugin();
        await host.InvokeQuickStartAsync();

        var window = _Show(host.Windows[0].Content);
        window.Close();
        await HeadlessAvalonia.SettleAsync();

        // The window's binding is a peephole — closing it lets go of the surface (F2.13/AC-1401: read fresh over
        // DiagramChannel.SessionBind each time, so there is no separate disposable object to assert on any more),
        // and the session behind it is untouched: it is still there to bind to, on the host's own session list.
        Assert.Contains(host.Sessions.OpenSessions, session => session.PaneId == "pane-a");
        Assert.Empty(host.Registry.ListSurfaces("pane-a"));

        plugin.Dispose();
    });

    [Fact]
    public Task WhenTheBoundSessionEnds_TheCouplingGoesAndTheWindowStaysWithAnExplanation() => HeadlessAvalonia.RunAsync(async () =>
    {
        var (plugin, host) = _StartPlugin();
        await host.InvokeQuickStartAsync();
        var content = host.Windows[0].Content;
        var window = _Show(content);

        host.EndSession("pane-a");
        await HeadlessAvalonia.SettleAsync();

        Assert.Null(host.Registry.ListSurfaces("pane-a").Single().Coupling);
        Assert.True(window.IsVisible);
        Assert.Contains("Werksessie", _CouplingText(content));
        Assert.Contains("has ended", _CouplingText(content), StringComparison.Ordinal);

        // The way back out: re-couple to another running session, named from the open-sessions list (AC-833).
        Assert.Contains(content.GetVisualDescendants().OfType<Button>(), button => Equals(button.Content, "Couple…") && button.IsVisible);

        window.Close();
        plugin.Dispose();
    });

    [Fact]
    public Task RenamingANodeOnTheSurface_WritesTheNewLabelIntoTheSource() => HeadlessAvalonia.RunAsync(async () =>
    {
        var (plugin, host, content, window, surfaceId) = await _OpenOnOneNodeAsync();

        _ClickCentre(content, window);
        _Button(content, "Rename").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await HeadlessAvalonia.SettleAsync();

        // The rename box sits on the node itself — the one editable text box on this window; the source box below is
        // still read-only (AC-811).
        var box = content.GetVisualDescendants().OfType<TextBox>().Single(candidate => !candidate.IsReadOnly);
        box.Text = "Hernoemd";
        box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        await HeadlessAvalonia.SettleAsync();

        Assert.Contains("A[\"Hernoemd\"]", host.Registry.PeekText(surfaceId)!, StringComparison.Ordinal);

        window.Close();
        plugin.Dispose();
    });

    // AC-891: the AC-835 core→plugin registry hand-off this covered (open_diagram asking, the registry carrying the
    // request over) is gone — DiagramMcpTools now opens the window directly, so that path is covered in
    // Cockpit.Plugin.Diagram.Tests/DiagramMcpToolsTests.cs, inside the assembly that owns both the tool and the window.

    private async Task<(ICockpitPlugin Plugin, RecordingHost Host, Control Content, Window Window, string SurfaceId)> _OpenOnOneNodeAsync()
    {
        var (plugin, host) = _StartPlugin();
        await host.InvokeQuickStartAsync();
        var content = host.Windows[0].Content;
        var window = _Show(content);
        var surfaceId = host.Registry.ListSurfaces("pane-a").Single().SurfaceId;

        host.Registry.UpdateText(surfaceId, "flowchart TD\n    A[\"Alleen\"]");
        await HeadlessAvalonia.SettleAsync();
        return (plugin, host, content, window, surfaceId);
    }

    private static Button _Button(Control content, string caption) =>
        content.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, caption));

    private static void _ClickCentre(Control content, Window window)
    {
        var point = _ViewportCentre(content, window);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private static Point _ViewportCentre(Control content, Window window)
    {
        var viewport = content.GetVisualDescendants().OfType<Border>().Last(border => border.ClipToBounds);
        var centre = new Point(viewport.Bounds.Width / 2, viewport.Bounds.Height / 2);
        return viewport.TranslatePoint(centre, window)
               ?? throw new InvalidOperationException("the diagram viewport must be laid out to be clicked");
    }

    // The body only reaches its own visual tree — and only fires DetachedFromVisualTree on close — inside a real
    // window, which is what this ticket puts it in.
    private static Window _Show(Control content)
    {
        var window = new Window { Content = content, Width = 900, Height = 640 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static string _CouplingText(Control content) =>
        string.Join(" ", content.GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text));

    private static (ICockpitPlugin Plugin, RecordingHost Host) _StartPlugin()
    {
        var folder = _LocatePluginOutput();
        Assert.NotNull(folder);

        var manifestJson = File.ReadAllText(Path.Combine(folder!, "plugin.json"));
        Assert.True(PluginManifest.TryParse(manifestJson, out var manifest, out _));

        // F2.13/AC-1401: the entry (backend) assembly specifically — manifest.Assemblies now also yields UiAssembly.
        var hash = PluginHash.Compute(File.ReadAllBytes(Path.Combine(folder, manifest!.EntryAssembly!)));
        var discovered = new DiscoveredPlugin(folder, "diagram", manifest, hash, PluginLoadDecision.Load);
        var plugin = new PluginActivator(NullLogger<PluginActivator>.Instance).Activate(discovered);
        Assert.NotNull(plugin);

        var host = new RecordingHost();
        plugin!.Initialize(host);
        DiagramPluginUi.Initialize(discovered, plugin, host, host.Hub, host.Surfaces);
        return (plugin, host);
    }

    // Same walk-up as DiagramPluginLoadTests: the plugin's own build output, either configuration.
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

    private sealed record OpenedWindow(string Title, string Key, Control Content);

    private sealed class RecordingHost : ICockpitHost
    {

        public RecordingHost()
        {
            var services = new ServiceCollection();
            services.AddSingleton<IDiagramAccessRegistry>(Registry);
            Services = services.BuildServiceProvider();
        }

        // The real registry, not a fake: exclusivity and the coupling lifecycle are its rules, and a stand-in
        // would only prove the plugin agrees with the stand-in.
        public DiagramAccessRegistry Registry { get; } = new();

        public List<OpenedWindow> Windows { get; } = [];

        // AC-1400: the plugin channel's hub; DiagramPluginUi hands the UI part the same one.
        public PluginChannelHub Hub { get; } = new(NullLogger<PluginChannelHub>.Instance);

        public IPluginBackendChannel Channel => Hub.For(DiagramPluginUi.PluginId);

        public IServiceProvider Services { get; }

        public ICockpitActions Actions { get; } = new NoActions();

        public IPluginStorage Storage { get; } = new MemoryStorage();

        public FakeSessions Sessions { get; } = new();

        ICockpitSessionObserver ICockpitHost.Sessions => Sessions;

        private Control? _listDialogContent;

        // AC-896's two-stage path: "Diagrams" opens the list dialog, "New diagram" in its header opens the
        // quick-start — the one entry point that already names a session, standing in for an operator who ticks
        // "couple to this session" and hits Enter on the prefilled name.
        public async Task InvokeQuickStartAsync()
        {
            await Surfaces.ToolbarActions[0].OnInvoke();

            // UserControl.Content only materialises into the visual tree once templated — shown, here, the same
            // way a document window's content already has to be for its own button lookups to find anything.
            var listWindow = new Window { Content = _listDialogContent };
            listWindow.Show();
            await HeadlessAvalonia.SettleAsync();
            _listDialogContent!.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "New diagram"))
                .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            listWindow.Close();

            // AC-1891: the window connects to its registry over the channel right after it is built.
            await HeadlessAvalonia.SettleAsync();
        }

        // What the cockpit does when a session closes: it releases that session's couplings (CockpitViewModel's
        // driver-side teardown) — every window bound to it hears about it over DiagramChannel.SessionClosed
        // (F2.13/AC-1401: SurfaceSessionBinding subscribes to the channel, not to a per-binding Ended event).
        public void EndSession(string paneId)
        {
            Registry.SessionEnded(paneId);
            Sessions.Close(paneId);
        }
        // What CockpitUiHost reaches: the toolbar actions land here and its dialogs are answered by _OnDialog.
        private RecordingUiSurfaces? _surfaces;

        public RecordingUiSurfaces Surfaces => _surfaces ??= new RecordingUiSurfaces { OnDialog = _OnDialog, ActivePaneId = "pane-a" };

        // Overridden rather than left to the default forwarder precisely because the key is what this ticket is
        // about — the real host (PluginDialogHost) keys on it too.
        private Task _OnDialog(string title, Func<Control> createContent, string singleInstanceKey)
        {
            var content = createContent();
            if (singleInstanceKey == "diagram.quickstart")
            {
                // Standing in for an operator who ticks "couple to this session" and hits Enter on the prefilled name.
                content.GetVisualDescendants().OfType<CheckBox>().Single().IsChecked = true;
                content.GetVisualDescendants().OfType<Button>().First(button => Equals(button.Content, "Open"))
                    .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                return Task.CompletedTask;
            }

            if (singleInstanceKey == "diagram.list")
            {
                // Not a document window — Windows means document windows only (InvokeQuickStartAsync clicks through it).
                _listDialogContent = content;
                return Task.CompletedTask;
            }

            Windows.Add(new OpenedWindow(title, singleInstanceKey, content));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeSessions : ICockpitSessionObserver
    {
        public event EventHandler<string>? SessionClosed;

        public void Close(string paneId) => SessionClosed?.Invoke(this, paneId);

        public string? ActiveSessionWorkingDirectory => null;

        public string? ActivePaneId => "pane-a";

        public IReadOnlyList<OpenCockpitSession> OpenSessions { get; } = [new("pane-a", "Werksessie"), new("pane-b", "Tweede sessie")];

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
        public bool HasActiveSession => true;

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
