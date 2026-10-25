namespace Cockpit.Plugin.Browser.UI.Share;

// One agent session's permission on one tab (AC-1714). Paused while the tab is on another origin than it was shared on.
internal sealed class TabGrant(string paneId, string sessionName, string tabId, string origin, string mode)
{
    public string PaneId { get; } = paneId;

    public string SessionName { get; } = sessionName;

    public string TabId { get; } = tabId;

    public string Mode { get; } = mode;

    public string Origin { get; internal set; } = origin;

    // The origin the tab moved to, while paused; null while the grant stands.
    public string? MovedTo { get; internal set; }

    public bool Paused => MovedTo is not null;

    public DateTimeOffset? LastRead { get; internal set; }
}

// Every grant of Raymond's tabs, keyed on (session, tab): another session, or the same one on another tab, has none.
// A grant ends when Raymond stops it, the tab closes or the session ends; nothing here expires by time.
internal sealed class TabGrantRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<(string PaneId, string TabId), TabGrant> _grants = [];

    public event Action? Changed;

    public void Share(string paneId, string sessionName, string tabId, string origin, string mode) =>
        _Change(() => _grants[(paneId, tabId)] = new TabGrant(paneId, sessionName, tabId, origin, mode));

    // Stop sharing, and the tab closing, take effect on the next call: there is nothing left to authorize.
    public void Stop(string tabId) => _Remove(key => key.TabId == tabId);

    public void TabClosed(string tabId) => Stop(tabId);

    public void SessionEnded(string paneId) => _Remove(key => key.PaneId == paneId);

    // Another origin pauses the grant until Raymond keeps sharing; back on the shared origin it stands again.
    public void Navigated(string tabId, string origin) => _Change(() =>
    {
        foreach (var grant in _grants.Values.Where(grant => grant.TabId == tabId))
        {
            grant.MovedTo = string.Equals(grant.Origin, origin, StringComparison.OrdinalIgnoreCase) ? null : origin;
        }
    });

    public void KeepSharing(string tabId) => _Change(() =>
    {
        foreach (var grant in _grants.Values.Where(grant => grant.TabId == tabId && grant.MovedTo is not null))
        {
            grant.Origin = grant.MovedTo!;
            grant.MovedTo = null;
        }
    });

    // The standing grant of exactly this session on exactly this tab, or null: the one check every read goes through.
    public TabGrant? Authorize(string paneId, string tabId)
    {
        lock (_gate)
        {
            return _grants.TryGetValue((paneId, tabId), out var grant) && grant.Paused == false ? grant : null;
        }
    }

    public void MarkRead(TabGrant grant, DateTimeOffset at) => _Change(() => grant.LastRead = at);

    public IReadOnlyList<TabGrant> ForTab(string tabId)
    {
        lock (_gate)
        {
            return [.. _grants.Values.Where(grant => grant.TabId == tabId)];
        }
    }

    public IReadOnlyList<TabGrant> All
    {
        get
        {
            lock (_gate)
            {
                return [.. _grants.Values];
            }
        }
    }

    public IReadOnlyList<TabGrant> ForSession(string paneId)
    {
        lock (_gate)
        {
            return [.. _grants.Values.Where(grant => grant.PaneId == paneId)];
        }
    }

    // A tab's origin as the grant compares it: scheme, host and port, so a path change on the same site never pauses.
    public static string OriginOf(string address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.IsAbsoluteUri && uri.Host.Length > 0
            ? uri.GetLeftPart(UriPartial.Authority)
            : address;

    private void _Remove(Func<(string PaneId, string TabId), bool> match) => _Change(() =>
    {
        foreach (var key in _grants.Keys.Where(match).ToList())
        {
            _grants.Remove(key);
        }
    });

    private void _Change(Action change)
    {
        lock (_gate)
        {
            change();
        }

        Changed?.Invoke();
    }
}
