using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;

namespace Cockpit.Infrastructure.Sessions;

// AC-2026: how many sessions run under a profile, panes and delegated tasks together, and the one section that admits
// a new one against `MaxParallelSessions`. Pane starts and delegations both pass through `TryAdmit`, so they cannot
// both slip in under the limit.
public sealed class ProfileSessionLimit(SessionRegistry registry) : ISingletonService
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, string> _reserved = new(StringComparer.Ordinal);
    private Func<string, int> _delegated = _ => 0;

    public void CountDelegationsWith(Func<string, int> count) => _delegated = count;

    public ProfileLoad Load(string profileLabel)
    {
        lock (_gate)
        {
            return _Load(profileLabel);
        }
    }

    public ProfileSessionsView ViewOf(SessionProfile profile)
    {
        var load = Load(profile.Label);
        return new ProfileSessionsView(profile.MaxParallelSessions, load.PaneTitles.Count, load.DelegatedTasks);
    }

    // Null when admitted; otherwise the reason. A pane id is reserved until `Release`, and `admit` runs inside the section.
    public string? TryAdmit(SessionProfile profile, string? paneId, Action? admit = null)
    {
        lock (_gate)
        {
            if (_FullReason(profile) is { } full)
            {
                return full;
            }

            if (paneId is not null)
            {
                _reserved[paneId] = profile.Label;
            }

            admit?.Invoke();
            return null;
        }
    }

    // Why a start under this profile would be refused right now; null when it would not.
    public string? FullReason(SessionProfile profile)
    {
        lock (_gate)
        {
            return _FullReason(profile);
        }
    }

    public void Release(string paneId)
    {
        lock (_gate)
        {
            _reserved.Remove(paneId);
        }
    }

    private string? _FullReason(SessionProfile profile)
    {
        var load = _Load(profile.Label);
        if (profile.MaxParallelSessions <= 0 || load.Total < profile.MaxParallelSessions)
        {
            return null;
        }

        var titles = load.PaneTitles.Count > 0 ? $": {string.Join(", ", load.PaneTitles)}" : string.Empty;
        return $"Profile '{profile.Label}' is at its maximum of {profile.MaxParallelSessions} parallel sessions " +
            $"({load.PaneTitles.Count} open{titles}; {load.DelegatedTasks} delegated). Close a session or wait for a task to finish.";
    }

    // A pane that awaits its start counts only once it is reserved by a start; a reserved pane counts once, registered or not.
    private ProfileLoad _Load(string label)
    {
        var panes = registry.All
            .Where(handle => !handle.IsTerminal
                && string.Equals(handle.ActiveProfileLabel, label, StringComparison.OrdinalIgnoreCase)
                && (handle is not IHostedSession { AwaitsStart: true } || _reserved.ContainsKey(handle.PaneId)))
            .ToDictionary(handle => handle.PaneId, handle => handle.Title, StringComparer.Ordinal);
        foreach (var (paneId, reservedLabel) in _reserved)
        {
            if (string.Equals(reservedLabel, label, StringComparison.OrdinalIgnoreCase))
            {
                panes.TryAdd(paneId, paneId);
            }
        }

        return new ProfileLoad([.. panes.Values], _delegated(label));
    }
}

// What runs under one profile now: open panes by title, and delegated tasks that are queued or running.
public sealed record ProfileLoad(IReadOnlyList<string> PaneTitles, int DelegatedTasks)
{
    public int Total => PaneTitles.Count + DelegatedTasks;
}
