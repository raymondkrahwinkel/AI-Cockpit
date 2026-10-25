using Avalonia.Controls;
using Cockpit.Plugin.Browser.UI.Engine;
using Cockpit.Plugin.Browser.UI.Runtime;

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
        ["browser-codec"] = () => _Shell(BrowserPlace.Tab, codecUnsupported: true),
        ["browser-engine-download"] = () => _Empty(BrowserEngine.ForScene(
            BrowserEngineState.Fetching,
            progress: new CefRuntimeProgress("Downloading", 61_000_000, 172_752_666),
            downloadText: "Browser engine: download ≈ 173 MB, ≈ 419 MB on disk")),
        ["browser-engine-missing"] = () => _Empty(BrowserEngine.ForScene(
            BrowserEngineState.Unavailable,
            "The downloaded CEF runtime does not match its pinned checksum (expected c81c34d048d8…, got 5e0f1a92c7b3…) and was discarded.")),
    };

    private static Control _Shell(BrowserPlace place, bool codecUnsupported = false)
    {
        var engine = BrowserEngine.ForScene(BrowserEngineState.Ready);
        var model = new BrowserModel(engine);
        model.AddStaged(_Tab(engine, "https://www.youtube.com/watch?v=jfKfPfyJRdk").Staged("Lofi beats to code to — YouTube", audible: true, codecUnsupported: codecUnsupported));
        model.AddStaged(_Tab(engine, "https://github.com/raymondkrahwinkel/AI-Cockpit").Staged("raymondkrahwinkel/AI-Cockpit"));
        model.AddStaged(_Tab(engine, "https://docs.avaloniaui.net/").Staged("Avalonia docs — WebView", muted: true));

        var home = new BrowserHome(new BrowserShell(model, _ => { }));
        home.MoveTo(place);
        return home.CreateSlot(place);
    }

    private static BrowserTab _Tab(BrowserEngine engine, string address) => new(CefBrowserView.ForScene(engine), address);

    private static Control _Empty(BrowserEngine engine)
    {
        var home = new BrowserHome(new BrowserShell(new BrowserModel(engine), _ => { }));
        return home.CreateSlot(BrowserPlace.Tab);
    }
}
