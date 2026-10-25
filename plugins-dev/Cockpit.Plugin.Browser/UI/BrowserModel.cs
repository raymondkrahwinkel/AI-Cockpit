using Cockpit.Plugin.Browser.UI.Engine;

namespace Cockpit.Plugin.Browser.UI;

// One tab of Raymond's profile. Every field follows its CefBrowserView, on the UI thread.
internal sealed class BrowserTab
{
    public BrowserTab(CefBrowserView view, string address)
    {
        View = view;
        Address = address;
        view.AddressChanged += value => _Update(() => Address = value);
        view.TitleChanged += value => _Update(() => Title = value);
        view.LoadingChanged += (loading, back, forward) => _Update(() => (IsLoading, CanGoBack, CanGoForward) = (loading, back, forward));
        view.AudibleChanged += value => _Update(() => IsAudible = value);
        view.CodecUnsupported += () => _Update(() => CodecUnsupported = true);
    }

    public CefBrowserView View { get; }

    public string Title { get; private set; } = "New tab";

    public string Address { get; private set; }

    public bool IsLoading { get; private set; }

    public bool CanGoBack { get; private set; }

    public bool CanGoForward { get; private set; }

    public bool IsAudible { get; private set; }

    public bool IsMuted => View.IsMuted;

    // Set by the page's media or DRM request, cleared by the next navigation or by dismissing the bar.
    public bool CodecUnsupported { get; set; }

    public event Action? Changed;

    public void ToggleMute() => _Update(() => View.SetMuted(View.IsMuted == false));

    public void Navigate(string address)
    {
        CodecUnsupported = false;
        View.Navigate(address);
    }

    private void _Update(Action change)
    {
        change();
        Changed?.Invoke();
    }
}

// The one set of tabs of Raymond's profile, shared by every place the browser shows (AC-1711).
internal sealed class BrowserModel(BrowserEngine engine)
{
    public const string StartAddress = "about:blank";

    private readonly List<BrowserTab> _tabs = [];

    public BrowserEngine Engine { get; } = engine;

    public IReadOnlyList<BrowserTab> Tabs => _tabs;

    public BrowserTab? Active { get; private set; }

    // The tab list or the active tab changed; a change inside a tab comes through BrowserTab.Changed.
    public event Action? Changed;

    public BrowserTab NewTab(string address = StartAddress)
    {
        var view = new CefBrowserView(Engine, address, Engine.RaymondProfile);
        var tab = new BrowserTab(view, address);
        view.PopupRequested += url => NewTab(url);
        _tabs.Add(tab);
        Active = tab;
        Changed?.Invoke();
        return tab;
    }

    public void Activate(BrowserTab tab)
    {
        Active = tab;
        Changed?.Invoke();
    }

    public void Close(BrowserTab tab)
    {
        var index = _tabs.IndexOf(tab);
        if (index < 0)
        {
            return;
        }

        _tabs.RemoveAt(index);
        _ = tab.View.CloseAsync();
        if (ReferenceEquals(Active, tab))
        {
            Active = _tabs.Count == 0 ? null : _tabs[Math.Min(index, _tabs.Count - 1)];
        }

        Changed?.Invoke();
    }
}
