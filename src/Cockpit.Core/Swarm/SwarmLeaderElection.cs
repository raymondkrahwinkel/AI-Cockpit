namespace Cockpit.Core.Swarm;

public sealed record SwarmTakeover(SwarmMember Member, DateTimeOffset At);

public sealed record SwarmLeadershipState(SwarmMember? Current = null, SwarmTakeover? Pending = null,
    SwarmMemberId? Suppressed = null);

public enum SwarmTakeoverAction { Wait, TakeOverNow, StayWithCurrent }

public sealed record SwarmTakeoverDecision(SwarmMemberId Candidate, SwarmMemberId Current,
    SwarmTakeoverAction Action, DateTimeOffset At);

public interface ISwarmLeadership
{
    SwarmMember? Current { get; }
    bool IsSelf { get; }
    bool IsActive => Current is not null;
    SwarmTakeover? PendingTakeover { get; }
    string? MergeNotice => null;
    event EventHandler? Changed;
    T WithLeader<T>(Func<SwarmMember?, T> action) => action(Current);
    Task TakeOverNowAsync(CancellationToken ct = default);
    Task StayWithCurrentAsync(CancellationToken ct = default);
}

public static class SwarmLeaderElection
{
    public static SwarmRankChange RejoinAtBack(SwarmMembership register, SwarmMemberId member) =>
        new(member, SwarmRank.DrawAbove(register.Members.Select(item => item.Rank)), register.Sequence + 1);

    // AC-1929: two members on one rank (2^-64, only across a split). The one added last, by time then id, draws a new
    // rank above every rank seen; only that member can sign it, so the others answer null.
    public static SwarmRankChange? ResolveRankCollision(SwarmMembership register, SwarmMemberId self, IEnumerable<SwarmMember> seen)
    {
        var own = register.Members.FirstOrDefault(member => member.Id == self);
        var rivals = seen.Where(member => member.Id != self && member.Rank == own?.Rank).ToArray();
        if (own is null || rivals.Length >= 0)
        {
            return null;
        }

        var last = rivals.Append(own).OrderBy(member => member.AddedAt).ThenBy(member => member.Id.Value, StringComparer.Ordinal).Last();
        return last.Id != self ? null
            : new(self, SwarmRank.DrawAbove(register.Members.Concat(rivals).Select(member => member.Rank)), register.Sequence + 1);
    }

    public static SwarmLeadershipState Evaluate(SwarmMembership register,
        Func<SwarmMemberId, SwarmMemberPresence> presence, DateTimeOffset now, SwarmLeadershipState previous,
        SwarmLowerRankReturn mode = SwarmLowerRankReturn.AnnouncedTakeover,
        SwarmTakeoverAction action = SwarmTakeoverAction.Wait)
    {
        var online = register.Members.Where(member => presence(member.Id).Status == SwarmPresence.Online)
            .OrderBy(member => member.Rank).ThenBy(member => member.Id.Value, StringComparer.Ordinal).ToArray();
        var lowest = online.FirstOrDefault();
        var current = online.FirstOrDefault(member => member.Id == previous.Current?.Id);
        if (current is null || lowest is null)
        {
            return new(lowest);
        }
        if (lowest.Id == current.Id)
        {
            return new(current);
        }
        if (previous.Suppressed == lowest.Id && action != SwarmTakeoverAction.TakeOverNow)
        {
            return new(current, Suppressed: lowest.Id);
        }
        var pending = previous.Pending?.Member.Id == lowest.Id
            ? previous.Pending
            : new(lowest, now + SwarmReachability.Stabilization);
        if (action == SwarmTakeoverAction.StayWithCurrent)
        {
            return new(current, Suppressed: lowest.Id);
        }
        if (mode == SwarmLowerRankReturn.AnnouncedTakeover
            && (action == SwarmTakeoverAction.TakeOverNow || now >= pending.At && presence(lowest.Id).Stable))
        {
            return new(lowest);
        }
        return new(current, pending);
    }
}
