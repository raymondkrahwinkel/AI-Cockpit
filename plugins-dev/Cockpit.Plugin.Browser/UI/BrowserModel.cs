using Avalonia.Media.Imaging;
using Avalonia.Input;
using Cockpit.Plugin.Browser.UI.Engine;

namespace Cockpit.Plugin.Browser.UI;

// One tab of the user's profile. Every field follows its CefBrowserView, on the UI thread.
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
        view.FaviconChanged += async url =>
        {
            var icon = await Favicons.GetAsync(url);
            _Update(() => Favicon = icon);
        };
    }

    public CefBrowserView View { get; }

    // AC-1714: what a grant and an agent name this tab by. Short and opaque; it says nothing about the page.
    public string Id { get; } = Guid.NewGuid().ToString("N")[..8];

    public string Title { get; private set; } = "New tab";

    public string Address { get; private set; }

    public bool IsLoading { get; private set; }

    public bool CanGoBack { get; private set; }

    public bool CanGoForward { get; private set; }

    public bool IsAudible { get; private set; }

    public bool IsMuted => View.IsMuted;

    internal string PersistenceAddress => _deferredAddress ?? Address;

    internal bool IsDeferred => _deferredAddress is not null;

    private string? _deferredAddress;

    public Bitmap? Favicon { get; private set; }

    // Set by the page's media or DRM request, cleared by the next navigation or by dismissing the bar.
    public bool CodecUnsupported { get; set; }

    public event Action? Changed;

    // A headless scene's tab: what the page would have reported, set at once.
    internal BrowserTab Staged(string title, Bitmap? favicon = null, bool audible = false, bool muted = false, bool codecUnsupported = false)
    {
        Title = title;
        Favicon = favicon;
        IsAudible = audible;
        CodecUnsupported = codecUnsupported;
        View.SetMuted(muted);
        return this;
    }

    public void ToggleMute() => _Update(() => View.SetMuted(View.IsMuted == false));

    public void Navigate(string address)
    {
        _deferredAddress = null;
        _Update(() =>
        {
            Address = address;
            CodecUnsupported = false;
        });
        View.Navigate(address);
    }

    internal BrowserTab Defer(string address, string title, bool muted)
    {
        _deferredAddress = address;
        Title = title;
        View.SetMuted(muted);
        return this;
    }

    internal void LoadDeferred()
    {
        if (_deferredAddress is { } address)
        {
            Navigate(address);
        }
    }

    private void _Update(Action change)
    {
        change();
        Changed?.Invoke();
    }
}

// The one set of tabs of the user's profile, shared by every place the browser shows (AC-1711).
internal sealed class BrowserModel(BrowserEngine engine, Func<string, BrowserTab>? createTab = null)
{
    public const string StartAddress = "about:blank";

    private readonly List<BrowserTab> _tabs = [];
    private readonly Stack<string> _closedAddresses = [];

    public BrowserEngine Engine { get; } = engine;

    public IReadOnlyList<BrowserTab> Tabs => _tabs;

    public BrowserTab? Active { get; private set; }

    // The tab list or the active tab changed; a change inside a tab comes through BrowserTab.Changed.
    public event Action? Changed;

    // AC-1714: a tab was closed, so whatever was shared of it ends now.
    public event Action<BrowserTab>? TabClosed;

    // Only on a running engine: without one, a new tab would ask CEF for a browser it cannot create.
    public void NewTab(string address = StartAddress)
    {
        if (Engine.State != BrowserEngineState.Ready)
        {
            return;
        }

        var tab = _CreateTab(address);
        _tabs.Add(tab);
        Active = tab;
        Changed?.Invoke();
    }

    internal void AddStaged(BrowserTab tab)
    {
        _tabs.Add(_Wire(tab));
        Active ??= tab;
        Changed?.Invoke();
    }

    public void Activate(BrowserTab tab)
    {
        Active = tab;
        tab.LoadDeferred();
        Changed?.Invoke();
    }

    internal void Restore(BrowserSavedSession saved)
    {
        foreach (var item in saved.Tabs.Where(item => Uri.TryCreate(item.Url, UriKind.Absolute, out _)))
        {
            _tabs.Add(_CreateTab(StartAddress).Defer(item.Url, item.Title, item.Muted));
        }

        if (_tabs.Count > 0)
        {
            Activate(_tabs[Math.Clamp(saved.ActiveTab, 0, _tabs.Count - 1)]);
        }
    }

    public void ActivateRelative(int offset)
    {
        if (Active is not { } active || _tabs.Count < 2)
        {
            return;
        }

        Activate(_tabs[(_tabs.IndexOf(active) + offset + _tabs.Count) % _tabs.Count]);
    }

    public void CloseActive()
    {
        if (Active is { } active)
        {
            Close(active);
        }
    }

    public void ReopenClosed()
    {
        if (_closedAddresses.TryPop(out var address))
        {
            NewTab(address);
        }
    }

    public bool HandleShortcut(Key key, KeyModifiers modifiers)
    {
        if (modifiers == KeyModifiers.Control)
        {
            switch (key)
            {
                case Key.T:
                    NewTab();
                    return true;
                case Key.W:
                    CloseActive();
                    return true;
                case Key.Tab:
                    ActivateRelative(1);
                    return true;
                case Key.L:
                    return true;
            }
        }
        else if (modifiers == (KeyModifiers.Control | KeyModifiers.Shift))
        {
            switch (key)
            {
                case Key.T:
                    ReopenClosed();
                    return true;
                case Key.Tab:
                    ActivateRelative(-1);
                    return true;
            }
        }

        return false;
    }

    public void Close(BrowserTab tab)
    {
        var index = _tabs.IndexOf(tab);
        if (index < 0)
        {
            return;
        }

        _tabs.RemoveAt(index);
        _closedAddresses.Push(tab.Address);
        TabClosed?.Invoke(tab);
        _ = tab.View.CloseAsync();
        if (ReferenceEquals(Active, tab))
        {
            Active = _tabs.Count == 0 ? null : _tabs[Math.Min(index, _tabs.Count - 1)];
        }

        Changed?.Invoke();
        if (_tabs.Count == 0)
        {
            NewTab();
        }
    }

    private BrowserTab _CreateTab(string address) => _Wire(createTab?.Invoke(address) ?? new BrowserTab(new CefBrowserView(Engine, address, Engine.UserProfile), address));

    private BrowserTab _Wire(BrowserTab tab)
    {
        tab.View.PopupRequested += url => NewTab(url);
        tab.Changed += () => Changed?.Invoke();
        return tab;
    }
}
