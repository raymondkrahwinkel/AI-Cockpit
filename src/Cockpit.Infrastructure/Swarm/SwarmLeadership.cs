using Cockpit.Core.Abstractions;
using Cockpit.Core.Swarm;

namespace Cockpit.Infrastructure.Swarm;

internal sealed class SwarmLeadership(SwarmMembershipStore store, SwarmSettingsStore settings,
    SwarmIdentity identity, SwarmReachability reachability) : ISwarmLeadership, ISingletonService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile SwarmLeadershipState _state = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<SwarmMemberId, SwarmMemberId> _leaders = new();
    private SwarmTakeoverDecision? _decision;
    private readonly Lock _decisionGate = new();
    internal SwarmTakeoverDecision? Decision { get { lock (_decisionGate) return _decision; } }
    internal TimeProvider Time { get; set; } = TimeProvider.System;
    public SwarmMember? Current => _state.Current;
    public bool IsSelf => Current?.Id == identity.MemberId;
    public SwarmTakeover? PendingTakeover => _state.Pending;
    public event EventHandler? Changed;

    internal void Observe(SwarmMemberId peer, SwarmMemberId? leader, SwarmTakeoverDecision? decision)
    {
        if (leader is { } id) _leaders[peer] = id;
        else _leaders.TryRemove(peer, out _);
        lock (_decisionGate)
        {
            if (decision is not null && (_decision is null || decision.At > _decision.At)) _decision = decision;
        }
    }

    public Task TakeOverNowAsync(CancellationToken ct = default) => RefreshAsync(SwarmTakeoverAction.TakeOverNow, ct);
    public Task StayWithCurrentAsync(CancellationToken ct = default) => RefreshAsync(SwarmTakeoverAction.StayWithCurrent, ct);

    internal async Task RefreshAsync(SwarmTakeoverAction action = SwarmTakeoverAction.Wait, CancellationToken ct = default, bool asleep = false)
    {
        bool changed;
        await _gate.WaitAsync(ct);
        try
        {
            var register = await store.LoadAsync(ct);
            var mode = (await settings.LoadAsync(ct)).WhenALowerRankComesBack;
            var now = Time.GetUtcNow();
            SwarmMemberPresence Presence(SwarmMemberId id) => id == identity.MemberId
                ? new(asleep ? SwarmPresence.Asleep : SwarmPresence.Online, now, true) : reachability.Get(id);
            var previous = _state;
            if (previous.Current is null)
            {
                var peer = register.Members.Where(member => member.Id != identity.MemberId && Presence(member.Id).Status == SwarmPresence.Online
                    && _leaders.TryGetValue(member.Id, out var leader) && leader == member.Id)
                    .MinBy(member => member.Rank);
                previous = new(peer);
            }
            else if (previous.Current.Id == identity.MemberId && previous.Pending is null)
            {
                var holder = register.Members.Where(member => member.Rank > previous.Current.Rank
                    && Presence(member.Id).Status == SwarmPresence.Online
                    && _leaders.TryGetValue(member.Id, out var leader) && leader == member.Id).MinBy(member => member.Rank);
                if (holder is not null) previous = new(holder);
            }
            if (previous.Pending is { } pending && previous.Current is { } current)
            {
                lock (_decisionGate)
                {
                    if (action != SwarmTakeoverAction.Wait)
                        _decision = new(pending.Member.Id, current.Id, action, now);
                    if (_decision is { } decision && decision.Candidate == pending.Member.Id && decision.Current == current.Id
                        && decision.At >= pending.At - SwarmReachability.Stabilization)
                        action = decision.Action;
                }
            }
            var next = SwarmLeaderElection.Evaluate(register, Presence, now, previous, mode, action);
            if (mode == SwarmLowerRankReturn.JoinAtBack && next.Pending?.Member.Id == identity.MemberId)
            {
                await store.Gate.WaitAsync(ct);
                try
                {
                    register = await store.LoadAsync(ct);
                    var record = identity.Sign(SwarmMembership.ToPayload(SwarmLeaderElection.RejoinAtBack(register, identity.MemberId)));
                    if (register.Accept(record) != SwarmMembershipVerdict.Accepted) throw new InvalidDataException("Swarm rank change refused.");
                    store.Save(register);
                }
                finally { store.Gate.Release(); }
                next = SwarmLeaderElection.Evaluate(register, Presence, now, previous, mode);
            }
            changed = next.Current?.Id != _state.Current?.Id || next.Current?.Rank != _state.Current?.Rank
                || next.Pending?.Member.Id != _state.Pending?.Member.Id || next.Pending?.At != _state.Pending?.At;
            _state = next;
        }
        finally { _gate.Release(); }
        if (changed) Changed?.Invoke(this, EventArgs.Empty);
    }
}
