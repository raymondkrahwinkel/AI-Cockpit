using Avalonia.Threading;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Browser.UI;

internal enum BrowserStartupMode
{
    RestorePreviousTabs,
    EmptyNewTab,
    StartPage,
}

internal sealed record BrowserSavedTab(string Url, string Title, bool Muted);

internal sealed record BrowserSavedSession(IReadOnlyList<BrowserSavedTab> Tabs, int ActiveTab);

// The host owns the plugin-scoped, atomically persisted storage; this only batches ordinary tab churn.
internal sealed class BrowserTabSession : IDisposable
{
    internal const string StartupModeKey = "startupMode";
    internal const string StartPageKey = "startPage";
    internal const string TabsKey = "savedTabs";

    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly IPluginStorage storage;
    private BrowserModel? _model;
    private bool _restored;

    public BrowserTabSession(IPluginStorage storage)
    {
        this.storage = storage;
        _saveTimer.Tick += (_, _) => SaveNow();
    }

    public BrowserStartupMode StartupMode => storage.Get<BrowserStartupMode?>(StartupModeKey) ?? BrowserStartupMode.RestorePreviousTabs;

    public string StartPage => storage.Get<string>(StartPageKey) ?? BrowserModel.StartAddress;

    public void Attach(BrowserModel model)
    {
        _model = model;
        model.Changed += ScheduleSave;
    }

    public void Restore(BrowserModel model)
    {
        if (_restored)
        {
            return;
        }

        _restored = true;
        switch (StartupMode)
        {
            case BrowserStartupMode.RestorePreviousTabs:
                if (storage.Get<BrowserSavedSession>(TabsKey) is { Tabs.Count: > 0 } saved)
                {
                    model.Restore(saved);
                }
                break;
            case BrowserStartupMode.StartPage:
                model.NewTab(StartPage);
                break;
        }
    }

    public void Configure(BrowserStartupMode mode, string startPage)
    {
        storage.Set(StartupModeKey, mode);
        storage.Set(StartPageKey, startPage);
        if (mode != BrowserStartupMode.RestorePreviousTabs)
        {
            _saveTimer.Stop();
            storage.Remove(TabsKey);
        }
    }

    private void ScheduleSave()
    {
        if (StartupMode == BrowserStartupMode.RestorePreviousTabs)
        {
            _saveTimer.Stop();
            _saveTimer.Start();
        }
    }

    private void SaveNow()
    {
        _saveTimer.Stop();
        if (_model is null || StartupMode != BrowserStartupMode.RestorePreviousTabs)
        {
            return;
        }

        var tabs = _model.Tabs.Select(tab => new BrowserSavedTab(tab.PersistenceAddress, tab.Title, tab.IsMuted)).ToList();
        if (tabs.Count == 0)
        {
            storage.Remove(TabsKey);
            return;
        }

        storage.Set(TabsKey, new BrowserSavedSession(tabs, _model.Active is null ? 0 : _model.Tabs.ToList().IndexOf(_model.Active)));
    }

    public void Dispose() => _saveTimer.Stop();
}
