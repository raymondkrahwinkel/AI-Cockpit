using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Cockpit.Plugin.Browser.UI.Engine;
using Cockpit.Plugin.Browser.UI.Runtime;
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
        ["browser-tab"] = () => _Shell(BrowserPlace.Tab),
        ["browser-rail"] = () => _Shell(BrowserPlace.Rail),
        // Cockpit hangs a name ending in -floating in its own dock-panel window, titled by the automation name.
        ["browser-floating"] = () => _Named(_Shell(BrowserPlace.Rail), "Browser"),
        // Mockup screen 5: the Agents side with an agent tab's live picture, and the same while Raymond has taken over.
        ["browser-agents"] = () => _Shell(BrowserPlace.Tab, agentSide: true),
        ["browser-agents-driving"] = () => _Shell(BrowserPlace.Tab, agentSide: true, driving: true),
        ["browser-codec"] = () => _Shell(BrowserPlace.Tab, codecUnsupported: true),
        ["browser-engine-download"] = () => _Empty(BrowserEngine.ForScene(
            BrowserEngineState.Fetching,
            progress: new CefRuntimeProgress("Downloading", 61_000_000, 172_752_666),
            downloadText: "Browser engine: download ≈ 173 MB, ≈ 419 MB on disk")),
        ["browser-engine-missing"] = () => _Empty(BrowserEngine.ForScene(
            BrowserEngineState.Unavailable,
            "The downloaded CEF runtime does not match its pinned checksum (expected c81c34d048d8…, got 5e0f1a92c7b3…) and was discarded.")),
    };

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
        };

    private static Control _Shell(BrowserPlace place, bool codecUnsupported = false, bool agentSide = false, bool driving = false)
    {
        var engine = BrowserEngine.ForScene(BrowserEngineState.Ready);
        var model = new BrowserModel(engine);
        model.AddStaged(_Tab(engine, "https://www.youtube.com/watch?v=jfKfPfyJRdk").Staged("Lofi beats to code to — YouTube", _Square(0xFFE5_2D27), audible: true, codecUnsupported: codecUnsupported));
        model.AddStaged(_Tab(engine, "https://github.com/raymondkrahwinkel/AI-Cockpit").Staged("raymondkrahwinkel/AI-Cockpit", _Square(0xFF8A_8F98)));
        model.AddStaged(_Tab(engine, "https://docs.avaloniaui.net/").Staged("Avalonia docs — WebView", _Square(0xFF2F_6FE4), muted: true));

        var agents = new AgentTabsModel(null);
        agents.Stage(
            [
                new AgentTab("pane-1", "AC-1720", "t1", "localhost:5173 — Login", "http://localhost:5173/login", driving),
                new AgentTab("pane-2", "Depot-docs", "t2", "docs.avaloniaui.net", "https://docs.avaloniaui.net/", false),
            ],
            AgentLoginPicture.Render(1440, 745),
            (1440, 745));
        var shell = new BrowserShell(model, agents, _ => { });
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
