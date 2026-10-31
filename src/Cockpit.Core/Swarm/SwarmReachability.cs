using System.Net;

namespace Cockpit.Core.Swarm;

public enum SwarmPresence { Online, Asleep, NotReachable }

public sealed record SwarmMemberPresence(SwarmPresence Status, DateTimeOffset? LastSeen, bool Stable);

public sealed class SwarmReachability(TimeProvider time)
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan Lease = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan Stabilization = TimeSpan.FromSeconds(30);
    private readonly Lock _gate = new();
    private readonly Dictionary<SwarmMemberId, Entry> _entries = [];

    public SwarmReachability() : this(TimeProvider.System) { }

    public SwarmMemberPresence Get(SwarmMemberId member)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(member, out var entry))
            {
                return new(SwarmPresence.NotReachable, null, false);
            }
            var live = time.GetUtcNow() - entry.LastSeen < Lease;
            var status = entry.Asleep ? SwarmPresence.Asleep : live ? SwarmPresence.Online : SwarmPresence.NotReachable;
            return new(status, entry.LastSeen, status == SwarmPresence.Online && time.GetUtcNow() - entry.Since >= Stabilization);
        }
    }

    public void Answer(SwarmMemberId member, HttpStatusCode response, bool asleep = false)
    {
        if ((int)response is < 200 or >= 300)
        {
            return;
        }
        lock (_gate)
        {
            var now = time.GetUtcNow();
            var since = _entries.TryGetValue(member, out var entry) && !entry.Asleep && now - entry.LastSeen < Lease ? entry.Since : now;
            _entries[member] = new(now, since, asleep);
        }
    }

    private sealed record Entry(DateTimeOffset LastSeen, DateTimeOffset Since, bool Asleep);
}
