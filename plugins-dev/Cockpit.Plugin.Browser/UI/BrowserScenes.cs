using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Cockpit.Plugin.Browser.UI.Engine;
using Cockpit.Plugin.Browser.UI.Runtime;
using Cockpit.Plugin.Browser.UI.Share;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Workspaces;
using Material.Icons;

namespace Cockpit.Plugin.Browser.UI;

// The real BrowserShell in fixed states for `--screenshot --scene plugin:<name>` (AC-1711). A dev hook, not a
// plugin contract: the engine is a stub that never fetches, and the page is a placeholder, since CEF does not run
// headless.
internal static class BrowserScenes
{
    public static IReadOnlyDictionary<string, Func<Control>> All { get; } = new Dictionary<string, Func<Control>>
    {
        ["browser-settings"] = () =>
        {
            var storage = new SceneStorage();
            return new BrowserEngineInfoControl(CefRuntimeCatalog.Load(), storage, new BrowserTabSession(storage));
        },
        ["browser-tab"] = () => _Shell(BrowserPlace.Tab),
        ["browser-rail"] = () => _Shell(BrowserPlace.Rail),
        // Cockpit hangs a name ending in -floating in its own dock-panel window, titled by the automation name.
        ["browser-floating"] = () => _Named(_Shell(BrowserPlace.Rail), "Browser"),
        // Mockup screen 5: the Agents side with an agent tab's live picture, and the same while Raymond has taken over.
        ["browser-agents"] = () => _Shell(BrowserPlace.Tab, agentSide: true),
        ["browser-agents-driving"] = () => _Shell(BrowserPlace.Tab, agentSide: true, driving: true),
        ["browser-codec"] = () => _Shell(BrowserPlace.Tab, codecUnsupported: true),
        // AC-1714, mockup screen 7: the active tab shared with one session, and the same tab after it moved to another site.
        ["browser-shared"] = () => _Shell(BrowserPlace.Tab, share: Shared.Reading),
        ["browser-share-paused"] = () => _Shell(BrowserPlace.Tab, share: Shared.Moved),
        // AC-1728, mockup v2 screens 10 to 12: a controlled tab, a form held for Raymond, and Raymond taking over.
        ["browser-tab-controlled"] = () => _Shell(BrowserPlace.Tab, share: Shared.Controlled),
        ["browser-form-held"] = () => _Shell(BrowserPlace.Tab, share: Shared.FormHeld),
        ["browser-took-over"] = () => _Shell(BrowserPlace.Tab, share: Shared.TookOver),
        ["browser-engine-download"] = () => _Empty(BrowserEngine.ForScene(
            BrowserEngineState.Fetching,
            progress: new CefRuntimeProgress("Downloading", 61_000_000, 172_752_666),
            downloadText: "Browser engine: download ≈ 173 MB, ≈ 419 MB on disk")),
        ["browser-engine-missing"] = () => _Empty(BrowserEngine.ForScene(
            BrowserEngineState.Unavailable,
            "The downloaded CEF runtime does not match its pinned checksum (expected c81c34d048d8…, got 5e0f1a92c7b3…) and was discarded.")),
    };

    private sealed class SceneStorage : IPluginStorage
    {
        private readonly Dictionary<string, object?> _values = [];

        public T? Get<T>(string key) => _values.TryGetValue(key, out var value) && value is T typed ? typed : default;

        public void Set<T>(string key, T value) => _values[key] = value;
    }

    // The browser as a workspace in Cockpit's main window, with its header button and status text (mockup screen 1).
    public static IReadOnlyDictionary<string, Func<(WorkspaceTypeRegistration Registration, string? StatusText)>> Workspaces { get; } =
        new Dictionary<string, Func<(WorkspaceTypeRegistration, string?)>>
        {
            ["browser-workspace"] = () => (
                new WorkspaceTypeRegistration(BrowserUi.WorkspaceTypeId, "Browser", _ => _Shell(BrowserPlace.Tab))
                {
                    IconKind = MaterialIconKind.Web,
                    HeaderActions = [new ToolbarAction("Dock in the rail", MaterialIconKind.DockRight, () => Task.CompletedTask)],
                },
                BrowserUi.StatusText(BrowserPlace.Tab, BrowserEngineState.Ready, 3)),
            ["browser-workspace-agents"] = () => (
                new WorkspaceTypeRegistration(BrowserUi.WorkspaceTypeId, "Browser", _ => _Shell(BrowserPlace.Tab, agentSide: true))
                {
                    IconKind = MaterialIconKind.Web,
                    HeaderActions = [new ToolbarAction("Dock in the rail", MaterialIconKind.DockRight, () => Task.CompletedTask)],
                },
                BrowserUi.StatusText(BrowserPlace.Tab, BrowserEngineState.Ready, 3, agentSide: true, agentTabs: 2)),
            // AC-1714, mockup screen 7: a shared tab in the main window, with what is shared in the status bar.
            ["browser-workspace-shared"] = () => (
                new WorkspaceTypeRegistration(BrowserUi.WorkspaceTypeId, "Browser", _ => _Shell(BrowserPlace.Tab, share: Shared.Reading))
                {
                    IconKind = MaterialIconKind.Web,
                    HeaderActions = [new ToolbarAction("Dock in the rail", MaterialIconKind.DockRight, () => Task.CompletedTask)],
                },
                BrowserUi.WithShared(
                    BrowserUi.StatusText(BrowserPlace.Tab, BrowserEngineState.Ready, 3),
                    [new TabGrant("pane-1714", "AC-1714 statusline", "scene", "https://github.com", TabShareService.Watch)])),
            // AC-1728, mockup v2 screen 10 in Cockpit's main window, with the controlled tab in the status bar.
            ["browser-workspace-controlled"] = () => (
                new WorkspaceTypeRegistration(BrowserUi.WorkspaceTypeId, "Browser", _ => _Shell(BrowserPlace.Tab, share: Shared.Controlled))
                {
                    IconKind = MaterialIconKind.Web,
                    HeaderActions = [new ToolbarAction("Dock in the rail", MaterialIconKind.DockRight, () => Task.CompletedTask)],
                },
                BrowserUi.WithShared(
                    BrowserUi.StatusText(BrowserPlace.Tab, BrowserEngineState.Ready, 3),
                    [new TabGrant("pane-1714", "AC-1714 statusline", "scene", "https://github.com", TabShareService.Control)])),
        };

    private enum Shared
    {
        None,
        Reading,
        Moved,
        Controlled,
        FormHeld,
        TookOver,
    }

    private static Control _Shell(BrowserPlace place, bool codecUnsupported = false, bool agentSide = false, bool driving = false, Shared share = Shared.None)
    {
        var engine = BrowserEngine.ForScene(BrowserEngineState.Ready);
        var model = new BrowserModel(engine);
        model.AddStaged(_Tab(engine, "https://www.youtube.com/watch?v=jfKfPfyJRdk").Staged("Lofi beats to code to — YouTube", _Square(0xFFE5_2D27), audible: true, codecUnsupported: codecUnsupported));
        model.AddStaged(_Tab(engine, "https://github.com/raymondkrahwinkel/AI-Cockpit").Staged("raymondkrahwinkel/AI-Cockpit", _Square(0xFF8A_8F98)));
        model.AddStaged(_Tab(engine, "https://docs.avaloniaui.net/").Staged("Avalonia docs — WebView", _Square(0xFF2F_6FE4), muted: true));

        var agents = new AgentTabsModel(null);
        agents.Stage(
            [
                new AgentTab("pane-1", "AC-1720 verify login", "t1", "localhost:5173 — Login", "http://localhost:5173/login", driving, Icon: _Square(0xFF2F_6FE4)),
                new AgentTab("pane-2", "Depot-docs bijwerken", "t2", "docs.avaloniaui.net", "https://docs.avaloniaui.net/", false, Icon: _Square(0xFF3F_A56B)),
            ],
            AgentLoginPicture.Render(1440, 745),
            (1440, 745));
                var grants = new TabGrantRegistry();
        if (share != Shared.None)
        {
            var pr = model.Tabs[1];
            model.Activate(pr);
            var control = share is Shared.Controlled or Shared.FormHeld or Shared.TookOver;
            grants.Share("pane-1714", "AC-1714 statusline", pr.Id, "https://github.com", control ? TabShareService.Control : TabShareService.Watch);
            grants.MarkRead(grants.ForTab(pr.Id)[0], DateTimeOffset.Now.AddSeconds(-12));
            if (share == Shared.Moved)
            {
                grants.Navigated(pr.Id, "https://accounts.google.com");
            }

            if (control)
            {
                grants.MarkAction(grants.ForTab(pr.Id)[0], "click \u201cRe-run failed jobs\u201d", DateTimeOffset.Now.AddSeconds(-3));
            }

            if (share == Shared.FormHeld)
            {
                grants.Hold(pr.Id, "POST https://github.com/raymondkrahwinkel/AI-Cockpit/actions/runs/18734/rerun");
            }

            if (share == Shared.TookOver)
            {
                grants.TakeOver(pr.Id);
            }
        }

        var shell = new BrowserShell(model, agents, _ => { }, grants, _ => { });
        if (agentSide)
        {
            shell.ShowAgents();
        }

        var home = new BrowserHome(shell);
        home.MoveTo(place);
        return home.CreateSlot(place);
    }

    private static Control _Named(Control control, string name)
    {
        AutomationProperties.SetName(control, name);
        return control;
    }

    // The mockup's coloured favicon squares, since a scene fetches nothing.
    private static Bitmap _Square(uint argb)
    {
        var bitmap = new WriteableBitmap(new PixelSize(16, 16), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var frame = bitmap.Lock();
        var pixels = new int[16 * 16];
        Array.Fill(pixels, unchecked((int)argb));
        Marshal.Copy(pixels, 0, frame.Address, pixels.Length);
        return bitmap;
    }

    private static BrowserTab _Tab(BrowserEngine engine, string address) => new(CefBrowserView.ForScene(engine), address);

    private static Control _Empty(BrowserEngine engine)
    {
        var home = new BrowserHome(new BrowserShell(new BrowserModel(engine), new AgentTabsModel(null), _ => { }));
        return home.CreateSlot(BrowserPlace.Tab);
    }
}
