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
using Cockpit.Plugins.Abstractions.Notifications;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.Workspaces;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-842's acceptance, driven through the real compiled Diagram plugin (which AC-836 folded the whiteboard into):
/// a whiteboard opens as its own window bound to the session already active, the coupling bar's three states, and
/// the board's own invite button asking a real Grant request rather than a silent Grant. Mirrors DiagramCollabWindowTests.
/// </summary>
[Collection("avalonia")]
public class WhiteboardCollabWindowTests
{
    [Fact]
    public void OpeningTwoWhiteboardsFromOneSession_BindsEachToThatSessionInItsOwnWindow() => HeadlessAvalonia.Run(() =>
    {
        var (plugin, host) = _StartPlugin();

        host.InvokeWhiteboardAction();
        host.InvokeWhiteboardAction();

        Assert.Equal(2, host.Windows.Count);
        Assert.NotEqual(host.Windows[0].Key, host.Windows[1].Key);
        Assert.All(host.Windows, window => Assert.StartsWith("whiteboard.document.", window.Key, StringComparison.Ordinal));

        var surfaces = host.Registry.ListSurfaces("pane-a");
        Assert.Equal(2, surfaces.Count);
        Assert.All(surfaces, surface => Assert.False(surface.Coupling!.CanRead));
        Assert.All(host.Bindings, binding => Assert.Equal("pane-a", binding.PaneId));

        _Show(host.Windows[0].Content);
        Assert.Contains("Werksessie", _CouplingText(host.Windows[0].Content));
    });

    [Fact]
    public void WhenTheBoundSessionEnds_TheCouplingGoesAndTheWindowStaysWithAnExplanation() => HeadlessAvalonia.Run(() =>
    {
        var (plugin, host) = _StartPlugin();
        host.InvokeWhiteboardAction();
        var content = host.Windows[0].Content;
        _Show(content);

        host.EndSession("pane-a");
        Dispatcher.UIThread.RunJobs();

        Assert.Null(host.Registry.ListSurfaces("pane-a").Single().Coupling);
        Assert.Contains("Werksessie", _CouplingText(content));
        Assert.Contains("afgelopen", _CouplingText(content), StringComparison.Ordinal);
        Assert.Contains(content.GetVisualDescendants().OfType<Button>(), button => Equals(button.Content, "Koppelen…") && button.IsVisible);
    });

    [Fact]
    public void InvitingWithoutConsent_LeavesNoAccess_ApprovingGrantsRead() => HeadlessAvalonia.Run(() =>
    {
        var (plugin, host) = _StartPlugin();
        host.ConsentOutcome = ConsentOutcome.Denied;
        host.InvokeWhiteboardAction();
        var content = host.Windows[0].Content;
        _Show(content);

        _ClickInvite(content);
        Dispatcher.UIThread.RunJobs();

        Assert.False(host.Registry.CouplingOf("pane-a", host.Registry.ListSurfaces("pane-a").Single().SurfaceId)!.CanRead);

        host.ConsentOutcome = ConsentOutcome.Approved;
        _ClickInvite(content);
        Dispatcher.UIThread.RunJobs();

        Assert.True(host.Registry.CouplingOf("pane-a", host.Registry.ListSurfaces("pane-a").Single().SurfaceId)!.CanRead);
        Assert.Equal(2, host.ConsentRequests.Count);
        Assert.Contains("screenshot", host.ConsentRequests[0].Action, StringComparison.OrdinalIgnoreCase);
    });

    [Fact]
    public void InvokingWithNoActiveSession_StillOpensTheBoard_WithNoAgentOnIt() => HeadlessAvalonia.Run(() =>
    {
        // W-2/AC-843 replaced AC-842's "no session, no window, a toast" with the diagram's quick-start shape: the
        // board opens on its name alone, and "no agent on this board" is a state the window itself draws.
        var (plugin, host) = _StartPlugin();
        host.Sessions = new FakeSessions(activePaneId: null);

        host.InvokeWhiteboardAction();

        var opened = Assert.Single(host.Windows);
        _Show(opened.Content);
        Assert.Contains("Geen agent gekoppeld", _CouplingText(opened.Content), StringComparison.Ordinal);
    });

    [Fact]
    public void DisconnectFromTheBoard_DropsTheCoupling() => HeadlessAvalonia.Run(() =>
    {
        var (plugin, host) = _StartPlugin();
        host.InvokeWhiteboardAction();
        var content = host.Windows[0].Content;
        _Show(content);
        var surfaceId = host.Registry.ListSurfaces("pane-a").Single().SurfaceId;
        host.Registry.Grant("pane-a", surfaceId);
        Dispatcher.UIThread.RunJobs();

        content.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Disconnect"))
            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.Null(host.Registry.CouplingOf("pane-a", surfaceId));
    });

    private static void _ClickInvite(Control content) =>
        content.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Laat sdk meekijken"))
            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

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
            services.AddSingleton<IWhiteboardAccessRegistry>(Registry);
            Services = services.BuildServiceProvider();
        }

        // The real registry, not a fake — exclusivity and the coupling lifecycle are its rules.
        public WhiteboardAccessRegistry Registry { get; } = new();

        public List<OpenedWindow> Windows { get; } = [];

        public List<FakeBinding> Bindings { get; } = [];

        public List<ConsentRequest> ConsentRequests { get; } = [];

        public List<string> Toasts { get; } = [];

        public ConsentOutcome ConsentOutcome { get; set; } = ConsentOutcome.Approved;

        public IServiceProvider Services { get; }

        public ICockpitActions Actions { get; } = new NoActions();

        public IPluginStorage Storage { get; } = new MemoryStorage();

        public ICockpitSessionObserver Sessions { get; set; } = new FakeSessions("pane-a");

        public void ShowToast(string message, PluginToastSeverity severity, string? actionLabel, Action? onAction) =>
            Toasts.Add(message);

        private Control? _listDialogContent;

        // AC-896's two-stage path: "Whiteboards" opens the list dialog, "Nieuw whiteboard" in its header opens the
        // quick-start (W-2/AC-843) — standing in for an operator clicking through both.
        public void InvokeWhiteboardAction()
        {
            _toolbarActions.Single(action => action.Title == "Whiteboards").OnInvoke().GetAwaiter().GetResult();

            // UserControl.Content only materialises into the visual tree once templated — shown, here, the same
            // way a document window's content already has to be for its own button lookups to find anything.
            var listWindow = new Window { Content = _listDialogContent };
            listWindow.Show();
            Dispatcher.UIThread.RunJobs();
            _listDialogContent!.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Nieuw whiteboard"))
                .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            listWindow.Close();
        }

        public void EndSession(string paneId)
        {
            Registry.SessionEnded(paneId);
            foreach (var binding in Bindings.Where(binding => binding.PaneId == paneId))
            {
                binding.End();
            }
        }

        public Task<ConsentDecision> RequestConsentAsync(ConsentRequest request)
        {
            ConsentRequests.Add(request);
            return Task.FromResult(new ConsentDecision(ConsentOutcome));
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

        public Task ShowDialogAsync(string title, Func<Control> createContent, string singleInstanceKey, double width = 720, double height = 560)
        {
            var content = createContent();
            if (singleInstanceKey == "whiteboard.quickstart")
            {
                // W-2/AC-843's snelstart, answered the way DiagramCollabWindowTests answers the diagram's: keep the
                // prefilled name, couple to the active session when there is one to couple to.
                var couple = content.GetVisualDescendants().OfType<CheckBox>().Single();
                couple.IsChecked = couple.IsEnabled;
                content.GetVisualDescendants().OfType<Button>().First(button => Equals(button.Content, "Openen"))
                    .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                return Task.CompletedTask;
            }

            if (singleInstanceKey == "whiteboard.list")
            {
                // Not a document window — Windows means document windows only (InvokeWhiteboardAction clicks through it).
                _listDialogContent = content;
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

    private sealed class FakeSessions(string? activePaneId) : ICockpitSessionObserver
    {
        public string? ActiveSessionWorkingDirectory => null;

        public string? ActivePaneId => activePaneId;

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
