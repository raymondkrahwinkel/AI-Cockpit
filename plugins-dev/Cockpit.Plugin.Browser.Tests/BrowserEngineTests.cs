using Avalonia.Input;
using System.Text.Json;
using Cockpit.Plugin.Browser.UI;
using Cockpit.Plugin.Browser.UI.Engine;
using Cockpit.Plugin.Browser.UI.Runtime;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Browser.Tests;

// AC-1711. Criterion 2 by hand as well: after a real start no port of the process tree listens (netstat, see the PR).
public class BrowserEngineTests
{
    // L2b (boundary): a debugging port would open every tab of every profile in the process to the whole machine. And
    // the view keeps only the keys that make no text (AC-1784): Avalonia drops the character of a handled key down,
    // so a handled letter never reached the page; the real typing, YouTube included, is measured in the PR.
    [Fact]
    public void TheEngineSettings_OpenNoDebuggingPort_AndIgnoreTheProcessCommandLine_AndTheViewLetsTextKeysThrough()
    {
        var settings = BrowserEngine.CreateSettings("runtime", "subprocess", "cache");

        Assert.Equal(0, settings.RemoteDebuggingPort);
        Assert.True(settings.CommandLineArgsDisabled);
        Assert.DoesNotContain(BrowserEngine.Switches, entry => entry.Key.StartsWith("remote-debugging", StringComparison.Ordinal));

        var view = CefBrowserView.ForScene(BrowserEngine.ForScene(BrowserEngineState.Ready));
        bool Handled(Key key, string? symbol, KeyModifiers modifiers = KeyModifiers.None)
        {
            var e = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeySymbol = symbol, KeyModifiers = modifiers };
            view.RaiseEvent(e);
            return e.Handled;
        }

        Assert.Equal(
            (false, false, false, true, true, true, true, true),
            (Handled(Key.A, "a"), Handled(Key.E, "ë"), Handled(Key.Space, " "), Handled(Key.Tab, "\t"), Handled(Key.Enter, "\r"),
                Handled(Key.Left, null), Handled(Key.V, "\u0016", KeyModifiers.Control), Handled(Key.R, "\u0012", KeyModifiers.Control)));
    }

    // L1 (UI): browser shortcuts stay in the shell's routed input path, never Cockpit's global shortcut dispatch.
    private static void _BrowserShortcutsStayLocal()
    {
        var engine = BrowserEngine.ForScene(BrowserEngineState.Ready);
        var model = new BrowserModel(engine, address => new BrowserTab(CefBrowserView.ForScene(engine), address));
        model.NewTab("https://one.test");

        void Press(Key key, KeyModifiers modifiers)
        {
            Assert.True(model.HandleShortcut(key, modifiers));
        }

        Press(Key.T, KeyModifiers.Control);
        Assert.Equal(2, model.Tabs.Count);
        Assert.Equal(BrowserModel.StartAddress, model.Active?.Address);

        Press(Key.W, KeyModifiers.Control);
        Assert.Single(model.Tabs);
        Assert.Equal("https://one.test", model.Active?.Address);

        Press(Key.T, KeyModifiers.Control | KeyModifiers.Shift);
        Assert.Equal(2, model.Tabs.Count);
        Press(Key.Tab, KeyModifiers.Control | KeyModifiers.Shift);
        Assert.Equal("https://one.test", model.Active?.Address);
        Press(Key.Tab, KeyModifiers.Control);
        Assert.Equal(BrowserModel.StartAddress, model.Active?.Address);
        Press(Key.L, KeyModifiers.Control);

        Press(Key.W, KeyModifiers.Control);
        Press(Key.W, KeyModifiers.Control);
        Assert.Equal((1, BrowserModel.StartAddress), (model.Tabs.Count, model.Active?.Address));

        var storage = new MemoryStorage();
        storage.Set(BrowserTabSession.TabsKey, new BrowserSavedSession(
            [new("https://one.test", "One", false), new("https://two.test", "Two", true), new("https://three.test", "Three", false)], 1));
        using var session = new BrowserTabSession(storage);
        var restored = new BrowserModel(engine, address => new BrowserTab(CefBrowserView.ForScene(engine), address));
        session.Restore(restored);
        Assert.Equal(new[] { "https://one.test", "https://two.test", "https://three.test" }, restored.Tabs.Select(tab => tab.PersistenceAddress));
        Assert.Equal("https://two.test", restored.Active?.PersistenceAddress);
        Assert.True(restored.Tabs[0].IsDeferred);
        Assert.False(restored.Active!.IsDeferred);
        session.Configure(BrowserStartupMode.EmptyNewTab, BrowserModel.StartAddress);
        Assert.Null(storage.Get<BrowserSavedSession>(BrowserTabSession.TabsKey));
    }

    // L2d (platform/fallback): an engine that cannot load is a reason on the panel, never an exception in Cockpit. A
    // failed fetch is tried again in the same process (AC-1760); once CEF itself failed to load, only a restart helps.
    [Fact]
    public async Task AnEngineThatCannotLoad_IsUnavailableWithTheReason_WithoutThrowing_AndAFailedFetchCanBeTriedAgain()
    {
        var root = Path.Combine(Path.GetTempPath(), "browser-engine-test-" + Guid.NewGuid().ToString("N"));
        var runtime = Directory.CreateDirectory(Path.Combine(root, "runtime")).FullName;
        var subprocess = Path.Combine(root, "subprocess.exe");
        var fetches = 0;
        try
        {
            File.WriteAllBytes(subprocess, []);
            var engine = new BrowserEngine((_, _) => Task.FromResult(++fetches == 1
                ? new CefRuntimeResult(null, "The download was interrupted.")
                : new CefRuntimeResult(runtime, null)), subprocess, Path.Combine(root, "cache"));

            await engine.EnsureStartedAsync();
            Assert.Equal(("The download was interrupted.", true), (engine.Reason, engine.CanRetry));

            await engine.RetryAsync();

            Assert.Equal(2, fetches);
            Assert.Equal(BrowserEngineState.Unavailable, engine.State);
            Assert.False(string.IsNullOrWhiteSpace(engine.Reason));
            Assert.False(engine.CanRetry);
            _BrowserShortcutsStayLocal();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class MemoryStorage : IPluginStorage
    {
        private readonly Dictionary<string, string> _values = [];

        public T? Get<T>(string key) => _values.TryGetValue(key, out var value) ? JsonSerializer.Deserialize<T>(value) : default;

        public void Set<T>(string key, T value) => _values[key] = JsonSerializer.Serialize(value);

        public void Remove(string key) => _values.Remove(key);
    }
}
