using Avalonia.Controls;
using Cockpit.Plugin.Browser.Ipc;
using Cockpit.Plugin.Browser.UI.Engine;
using Cockpit.Plugin.Browser.UI.Runtime;
using Cockpit.Plugin.Browser.UI.Share;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Docking;
using Cockpit.Plugins.Abstractions.UI;
using Cockpit.Plugins.Abstractions.Workspaces;
using Material.Icons;

namespace Cockpit.Plugin.Browser.UI;

// The UI half of the browser plugin: the engine's version in the settings (AC-1727) and the user's browser as a tab
// and in the rail, one browser in either place (AC-1711). The runtime owns its engine host until disposal.
public sealed class BrowserUi : ICockpitPluginUi, IDisposable, IAsyncDisposable
{
    public const string WorkspaceTypeId = "browser.tab";

    public const string DockPanelId = "browser.rail";

    private BrowserHome? _home;
    private AgentTabsModel? _agents;
    private BrowserModel? _model;
    private BrowserTabSession? _tabSession;
    private TabShareService? _share;
    private BrowserEngine? _engine;
    private Action? _unsubscribe;
    private bool _disposed;

    // Read by Cockpit's `--screenshot --scene plugin:<name>` on a dev checkout only; not part of the plugin contract.
    public static IReadOnlyDictionary<string, Func<Control>> Scenes => BrowserScenes.All;

    public static IReadOnlyDictionary<string, Func<(WorkspaceTypeRegistration Registration, string? StatusText)>> WorkspaceScenes => BrowserScenes.Workspaces;

    public void InitializeUi(ICockpitUiHost host)
    {
        _engine = BrowserEngine.Create();
        _engine.ValidatePackage();
        _engine.IsHardwareAccelerationEnabled = new BrowserSettings(host.Storage).IsHardwareAccelerationEnabled;
        _agents = new AgentTabsModel(host.Channel);
        // AC-1714: up front, so an agent asking before the browser was ever opened gets a refusal at once, not a timeout.
        _model = new BrowserModel(_engine);
        _tabSession = new BrowserTabSession(host.Storage);
        _tabSession.Attach(_model);
        _share = new TabShareService(host, _model, new TabGrantRegistry());
        host.AddSettings(() => new BrowserEngineInfoControl(CefRuntimeCatalog.Load(), host.Storage, _tabSession));
        host.AddWorkspaceType(new WorkspaceTypeRegistration(WorkspaceTypeId, "Browser", _ => _Home(host).CreateSlot(BrowserPlace.Tab))
        {
            IconKind = MaterialIconKind.Web,
            Description = "Browse the web with your own profile",
            HeaderActions = [new ToolbarAction("Dock in the rail", MaterialIconKind.DockRight, () =>
            {
                _Home(host).DockInRail();
                return Task.CompletedTask;
            })],
        });

        // KeepAlive: one slot for the plugin's lifetime, so collapsing the rail or floating it never rebuilds the page.
        host.AddDockPanel(new DockPanelRegistration(DockPanelId, "Browser", MaterialIconKind.Web, () => _Home(host).CreateSlot(BrowserPlace.Rail))
        {
            KeepAlive = true,
            Badge = _agents.Badge,
            CanFloat = true,
        });

        // Only approved runtime teardown disposes this instance; a shutdown request may still be cancelled.
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        var errors = new List<Exception>();
        BrowserIpcCleanup.Try(() => _share?.Dispose(), errors);
        BrowserIpcCleanup.Try(() => _unsubscribe?.Invoke(), errors);
        BrowserIpcCleanup.Try(() => _home?.Shell.Dispose(), errors);
        BrowserIpcCleanup.Try(() => _tabSession?.Dispose(), errors);
        BrowserIpcCleanup.Try(() => _agents?.Dispose(), errors);
        BrowserIpcCleanup.Try(() => _engine?.Shutdown(), errors);
        if (errors.Count != 0)
        {
            throw new AggregateException("Browser UI cleanup failed.", errors);
        }
    }

    internal Task ShutdownCompletion => _engine?.ShutdownCompletion ?? Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        try
        {
            Dispose();
        }
        finally
        {
            await ShutdownCompletion;
        }
    }

    // The agent side is the only thing the status bar still says about the browser; where it sits and how many
    // tabs it has are noise you read off the window itself.
    internal static string? StatusText(BrowserPlace place, BrowserEngineState state, int tabs, bool agentSide = false, int agentTabs = 0) =>
        agentSide ? $"Agent profile · {agentTabs} {(agentTabs == 1 ? "tab" : "tabs")} by agents" : null;

    // AC-1714, mockup screen 7: what is shared, on its own base text since StatusText no longer says anything.
    internal static string? WithShared(string? text, IReadOnlyList<TabGrant> grants)
    {
        if (grants.Count == 0)
        {
            return text;
        }

        var tabs = grants.Select(grant => grant.TabId).Distinct().Count();
        var controllers = grants.Where(grant => grant.Controls).ToList();
        var baseText = text ?? "Browser";
        return controllers.Count > 0
            ? $"{baseText} · {controllers.Count} {(controllers.Count == 1 ? "tab" : "tabs")} controlled by {string.Join(", ", controllers.Select(grant => grant.SessionName).Distinct())}"
            : $"{baseText} · {tabs} {(tabs == 1 ? "tab" : "tabs")} shared with {string.Join(", ", grants.Select(grant => grant.SessionName).Distinct())}";
    }

    private BrowserHome _Home(ICockpitUiHost host)
    {
        if (_home is not null)
        {
            return _home;
        }

        BrowserShell? shell = null;
        var share = _share!;
        shell = new BrowserShell(_model!, _agents!, url =>
        {
            if (TopLevel.GetTopLevel(shell) is { } top)
            {
                _ = top.Launcher.LaunchUriAsync(new Uri(url));
            }
        }, share.Grants, tab => _ = share.ShareAsync(tab), _tabSession);
        var home = new BrowserHome(shell);
        // An agent put a tab in front of you (AC-1875): the Browser on screen, on the Agents side, at that tab.
        if (_agents is not null)
        {
            _agents.ShowRequested += () =>
            {
                shell.ShowAgents();
                _ = home.Place == BrowserPlace.Rail ? host.OpenDockPanelAsync(DockPanelId) : host.OpenWorkspaceAsync(WorkspaceTypeId);
            };
        }

        home.Moved += place => _ = place == BrowserPlace.Rail ? host.OpenDockPanelAsync(DockPanelId) : host.OpenWorkspaceAsync(WorkspaceTypeId);
        void ShowStatus()
        {
            if (_disposed == false && _agents is { } agents)
            {
                host.SetStatusText(WithShared(
                    StatusText(home.Place, shell.Model.Engine.State, shell.Model.Tabs.Count, agents.Showing, agents.Tabs.Count),
                    share.Grants.All));
            }
        }
        void PostStatus() => Avalonia.Threading.Dispatcher.UIThread.Post(ShowStatus);
        void EngineChanged(object? sender, EventArgs args) => PostStatus();
        home.Changed += ShowStatus;
        share.Grants.Changed += PostStatus;
        _agents!.Changed += ShowStatus;
        shell.Model.Changed += ShowStatus;
        shell.Model.Engine.Changed += EngineChanged;
        _unsubscribe = () =>
        {
            home.Changed -= ShowStatus;
            share.Grants.Changed -= PostStatus;
            _agents.Changed -= ShowStatus;
            shell.Model.Changed -= ShowStatus;
            shell.Model.Engine.Changed -= EngineChanged;
        };
        _home = home;
        return home;
    }
}
