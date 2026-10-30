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
    SwarmTakeover? PendingTakeover { get; }
    event EventHandler? Changed;
    Task TakeOverNowAsync(CancellationToken ct = default);
    Task StayWithCurrentAsync(CancellationToken ct = default);
}

public static class SwarmLeaderElection
{
    public static SwarmRankChange RejoinAtBack(SwarmMembership register, SwarmMemberId member) =>
        new(member, SwarmRank.DrawAbove(register.Members.Select(item => item.Rank)), register.Sequence + 1);

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
