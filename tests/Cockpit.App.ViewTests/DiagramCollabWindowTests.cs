using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Cockpit.App.Plugins;
using Cockpit.Core.Abstractions.Diagrams;
using Cockpit.Core.Plugins;
using Cockpit.Infrastructure.Diagrams;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.Workspaces;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-834's acceptance, driven through the real compiled Diagram plugin: a diagram opens as its own window bound to
/// a session that is already running, one window per document, and nothing here starts or ends a session.
/// </summary>
[Collection("avalonia")]
public class DiagramCollabWindowTests
{
    [Fact]
    public void OpeningTwoDiagramsFromOneSession_BindsEachToThatSessionInItsOwnWindow() => HeadlessAvalonia.Run(() =>
    {
        var (plugin, host) = _StartPlugin();

        host.InvokeQuickStart();
        host.InvokeQuickStart();

        // Two documents, two windows, two distinct keys — SurfaceWindows folds on the key, so equal keys here
        // would mean the second diagram silently replaced the first.
        Assert.Equal(2, host.Windows.Count);
        Assert.NotEqual(host.Windows[0].Key, host.Windows[1].Key);
        Assert.All(host.Windows, window => Assert.StartsWith("diagram.document.", window.Key, StringComparison.Ordinal));

        // The window's pane id is the coupling bar's session id: both surfaces are coupled to the one session the
        // quick-start named, and neither was granted a capability by opening (AC-810 still asks for those).
        var surfaces = host.Registry.ListSurfaces("pane-a");
        Assert.Equal(2, surfaces.Count);
        Assert.All(surfaces, surface => Assert.False(surface.Coupling!.HasAnyCapability));
        Assert.All(host.Bindings, binding => Assert.Equal("pane-a", binding.PaneId));
        Assert.All(surfaces, surface => Assert.Contains(host.Windows, window => window.Key == $"diagram.document.{surface.SurfaceId}"));

        // …and the bar says so, with the session's operator-visible name rather than its raw pane id.
        var window = _Show(host.Windows[0].Content);
        Assert.Contains("Werksessie", _CouplingText(host.Windows[0].Content));

        window.Close();
        plugin.Dispose();
    });

    [Fact]
    public void ClosingTheWindow_LetsItsSessionKeepRunning() => HeadlessAvalonia.Run(() =>
    {
        var (plugin, host) = _StartPlugin();
        host.InvokeQuickStart();

        var window = _Show(host.Windows[0].Content);
        window.Close();
        Dispatcher.UIThread.RunJobs();

        // The binding is a peephole — it is let go of, and the session behind it is untouched.
        Assert.True(host.Bindings[0].IsDisposed);
        Assert.True(host.Bindings[0].IsLive);
        Assert.Empty(host.Registry.ListSurfaces("pane-a"));

        plugin.Dispose();
    });

    [Fact]
    public void WhenTheBoundSessionEnds_TheCouplingGoesAndTheWindowStaysWithAnExplanation() => HeadlessAvalonia.Run(() =>
    {
        var (plugin, host) = _StartPlugin();
        host.InvokeQuickStart();
        var content = host.Windows[0].Content;
        var window = _Show(content);

        host.EndSession("pane-a");
        Dispatcher.UIThread.RunJobs();

        Assert.Null(host.Registry.ListSurfaces("pane-a").Single().Coupling);
        Assert.True(window.IsVisible);
        Assert.Contains("Werksessie", _CouplingText(content));
        Assert.Contains("afgelopen", _CouplingText(content), StringComparison.Ordinal);

        // The way back out: re-couple to another running session, named from the open-sessions list (AC-833).
        Assert.Contains(content.GetVisualDescendants().OfType<Button>(), button => Equals(button.Content, "Koppelen…") && button.IsVisible);

        window.Close();
        plugin.Dispose();
    });

    [Fact]
    public void ClickingANodeOnTheSurface_HoldsItAndLetsTheOperatorRemoveIt_WithoutOpeningTheSource() => HeadlessAvalonia.Run(() =>
    {
        // AC-841: hand-editing happens on the render itself. One node, fit to the window, so the middle of the
        // viewport is that node — no coordinate arithmetic to keep in step with the layout.
        var (plugin, host) = _StartPlugin();
        host.InvokeQuickStart();
        var content = host.Windows[0].Content;
        var window = _Show(content);
        var surfaceId = host.Registry.ListSurfaces("pane-a").Single().SurfaceId;

        host.Registry.UpdateText(surfaceId, "flowchart TD\n    A[\"Alleen\"]");
        Dispatcher.UIThread.RunJobs();

        var node = _ViewportCentre(content, window);
        window.MouseDown(node, MouseButton.Left);
        window.MouseUp(node, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        // The click landed on the node the source calls A: it is now the operator's, and the agent's edit naming it
        // is refused while it is (AC-852's hold).
        Assert.True(host.Registry.IsHeldByOperator(surfaceId, "A"));
        Assert.Contains("jij bewerkt", _CouplingText(content), StringComparison.Ordinal);

        var delete = content.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Verwijderen"));
        Assert.True(delete.IsEnabled);
        delete.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        // One change, straight into the registry — and the source box it went past is still read-only (AC-811).
        Assert.DoesNotContain("A[", host.Registry.PeekText(surfaceId)!, StringComparison.Ordinal);
        Assert.All(content.GetVisualDescendants().OfType<TextBox>(), box => Assert.True(box.IsReadOnly));

        window.Close();
        plugin.Dispose();
    });

    [Fact]
    public void RenamingANodeOnTheSurface_WritesTheNewLabelIntoTheSource() => HeadlessAvalonia.Run(() =>
    {
        var (plugin, host, content, window, surfaceId) = _OpenOnOneNode();

        _ClickCentre(content, window);
        _Button(content, "Hernoemen").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        // The rename box sits on the node itself — the one editable text box on this window; the source box below is
        // still read-only (AC-811).
        var box = content.GetVisualDescendants().OfType<TextBox>().Single(candidate => !candidate.IsReadOnly);
        box.Text = "Hernoemd";
        box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("A[\"Hernoemd\"]", host.Registry.PeekText(surfaceId)!, StringComparison.Ordinal);

        window.Close();
        plugin.Dispose();
    });

    [Fact]
    public void ConnectingOnTheSurface_TakesTwoClicksInAnExplicitMode() => HeadlessAvalonia.Run(() =>
    {
        var (plugin, host, content, window, surfaceId) = _OpenOnOneNode();

        // Nothing happens on a click until the mode is on: that is what keeps panning and editing apart.
        _ClickCentre(content, window);
        Assert.DoesNotContain("-->", host.Registry.PeekText(surfaceId)!, StringComparison.Ordinal);

        _Button(content, "Verbinden").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        _ClickCentre(content, window);
        _ClickCentre(content, window);

        Assert.Contains("A --> A", host.Registry.PeekText(surfaceId)!, StringComparison.Ordinal);

        window.Close();
        plugin.Dispose();
    });

    private (ICockpitPlugin Plugin, RecordingHost Host, Control Content, Window Window, string SurfaceId) _OpenOnOneNode()
    {
        var (plugin, host) = _StartPlugin();
        host.InvokeQuickStart();
        var content = host.Windows[0].Content;
        var window = _Show(content);
        var surfaceId = host.Registry.ListSurfaces("pane-a").Single().SurfaceId;

        host.Registry.UpdateText(surfaceId, "flowchart TD\n    A[\"Alleen\"]");
        Dispatcher.UIThread.RunJobs();
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

        var hash = PluginHash.Compute(File.ReadAllBytes(Path.Combine(folder, manifest!.EntryAssembly)));
        var discovered = new DiscoveredPlugin(folder, "diagram", manifest, hash, PluginLoadDecision.Load);
        var plugin = new PluginActivator(NullLogger<PluginActivator>.Instance).Activate(discovered);
        Assert.NotNull(plugin);

        var host = new RecordingHost();
        plugin!.Initialize(host);
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
        private readonly List<ToolbarAction> _toolbarActions = [];

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

        public List<FakeBinding> Bindings { get; } = [];

        public IServiceProvider Services { get; }

        public ICockpitActions Actions { get; } = new NoActions();

        public IPluginStorage Storage { get; } = new MemoryStorage();

        public ICockpitSessionObserver Sessions { get; } = new FakeSessions();

        // "Nieuw diagram" — the one entry point that already names a session, standing in for an operator who
        // ticks "couple to this session" and hits Enter on the prefilled name.
        public void InvokeQuickStart() => _toolbarActions[0].OnInvoke().GetAwaiter().GetResult();

        // Both halves of what the cockpit does when a session closes: it releases that session's couplings
        // (CockpitViewModel's driver-side teardown) and every binding on it reports Ended.
        public void EndSession(string paneId)
        {
            Registry.SessionEnded(paneId);
            foreach (var binding in Bindings.Where(binding => binding.PaneId == paneId))
            {
                binding.End();
            }
        }

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

        public void AddToolbarAction(ToolbarAction action) => _toolbarActions.Add(action);

        public Task OpenWorkspaceAsync(string workspaceTypeId) => Task.CompletedTask;

        public Task ShowDialogAsync(string title, Func<Control> createContent, double width = 720, double height = 560) =>
            ShowDialogAsync(title, createContent, singleInstanceKey: "", width, height);

        // Overridden rather than left to the default forwarder precisely because the key is what this ticket is
        // about — the real host (PluginDialogHost) keys on it too.
        public Task ShowDialogAsync(string title, Func<Control> createContent, string singleInstanceKey, double width = 720, double height = 560)
        {
            var content = createContent();
            if (singleInstanceKey == "diagram.quickstart")
            {
                // Standing in for an operator who ticks "couple to this session" and hits Enter on the prefilled name.
                content.GetVisualDescendants().OfType<CheckBox>().Single().IsChecked = true;
                content.GetVisualDescendants().OfType<Button>().First(button => Equals(button.Content, "Openen"))
                    .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                return Task.CompletedTask;
            }

            Windows.Add(new OpenedWindow(title, singleInstanceKey, content));
            return Task.CompletedTask;
        }

        public IPluginSessionBinding BindToSession(string paneId)
        {
            var binding = new FakeBinding(paneId);
            Bindings.Add(binding);
            return binding;
        }
    }

    // Live for the one pane the fake cockpit is running, detached for anything else — the same split
    // CockpitHost.BindToSession makes.
    internal sealed class FakeBinding(string paneId) : IPluginSessionBinding
    {
        public string PaneId => paneId;

        public string? SessionName => IsLive ? "Werksessie" : null;

        public bool IsLive => paneId == "pane-a";

        public bool IsDisposed { get; private set; }

        public event EventHandler? Ended;

        public Task SendAsync(string text) => Task.CompletedTask;

        public void End() => Ended?.Invoke(this, EventArgs.Empty);

        public void Dispose() => IsDisposed = true;
    }

    private sealed class FakeSessions : ICockpitSessionObserver
    {
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
