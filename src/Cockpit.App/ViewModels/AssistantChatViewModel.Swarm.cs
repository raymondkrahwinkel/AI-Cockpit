using Cockpit.Core.Assistant;
using Cockpit.App.Services;

namespace Cockpit.App.ViewModels;

public sealed partial class AssistantChatViewModel
{
    private bool _opened;
    private bool _disposed;
    private int _leaderVersion;
    private string? _leaderId;
    private string? _leaderSessionError;

    private bool IsFollowingLeader => _cockpit?.SwarmLeadership is { IsActive: true, IsSelf: false };
    public string? LeadershipLabel => _cockpit?.AssistantLeadershipLabel;
    public string? LocationLabel => _cockpit?.AssistantLocationLabel;
    public bool LeaderIsServer => _cockpit?.AssistantLeaderIsServer == true;
    public string? LeaderSessionError => _leaderSessionError;
    public bool CanClearConversation => !IsFollowingLeader;

    internal async Task RefreshLeaderSessionAsync(CancellationToken cancellationToken = default)
    {
        var version = ++_leaderVersion;
        var leadership = _cockpit?.SwarmLeadership;
        var leader = leadership?.Current;
        _leaderId = leader?.Id.Value;
        _leaderSessionError = null;
        OnPropertyChanged(nameof(LeaderSessionError));
        _SetSession(IsFollowingLeader ? null : _host.View());
        try
        {
            await _host.EnsureStartedAsync(cancellationToken).ConfigureAwait(true);
            if (_disposed || version != _leaderVersion) return;
            if (!IsFollowingLeader)
            {
                _SetSession(_host.View());
                return;
            }
            if (leader is null || _cockpit?.SwarmSessions is not { } nodes) return;
            var handle = await nodes.OpenSessionAsync(leader.Name, AssistantIdentity.PaneId, cancellationToken).ConfigureAwait(true);
            if (_disposed || version != _leaderVersion) return;
            if (handle is null)
            {
                _leaderSessionError = $"Waiting for the assistant on {leader.Name}. You can keep typing here.";
                return;
            }
            var session = await SessionViewModel.OverRemoteAsync(handle, leader.Name).ConfigureAwait(true);
            if (_disposed || version != _leaderVersion)
            {
                await session.DisposeAsync();
                return;
            }
            session.SetRemoteLink(isUp: true, mayAnswerPermissions: true, allowsForSession: false);
            _SetSession(session);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (!_disposed && version == _leaderVersion)
                _leaderSessionError = $"Could not open the assistant on {leader?.Name ?? "the leader"}: {exception.Message}";
        }
        finally
        {
            if (!_disposed && version == _leaderVersion) OnPropertyChanged(nameof(LeaderSessionError));
        }
    }

    private void _SetSession(SessionViewModel? session)
    {
        if (ReferenceEquals(_observedSession, session)) return;
        var previous = _observedSession;
        _observedSession = session;
        _WatchTranscript(previous, session);
        _WatchThreads(previous, session);
        OnPropertyChanged(nameof(Session));
        OnPropertyChanged(nameof(HasSession));
        OnPropertyChanged(nameof(HasMessages));
        if (previous?.RemoteServer is not null) _ = previous.DisposeAsync();
    }
}
