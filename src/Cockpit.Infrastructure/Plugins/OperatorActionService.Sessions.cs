using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Workspaces;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Plugins.Abstractions.Channels;
using Microsoft.Extensions.Logging;

namespace Cockpit.Infrastructure.Plugins;

public sealed partial class OperatorActionService
{
    private readonly ISessionLauncher? _launcher;
    private readonly ISessionProfileStore? _profiles;
    private readonly Dictionary<(string Device, string Request), _Start> _starts = [];
    private readonly string _sessionEpoch = Guid.NewGuid().ToString("N");

    private bool _CanManage(_Channel channel, string capability)
    {
        lock (_gate)
        {
            return McpRequestContext.CurrentCaller is null && _channels.Contains(channel)
                && channel.CanManageSessions && capability == channel.SessionCapability;
        }
    }

    private async Task<OperatorSessionCatalog> _SessionCatalogAsync(_Channel channel)
    {
        if (!_CanManage(channel, channel.SessionCapability) || _launcher is null || _profiles is null)
        {
            throw new UnauthorizedAccessException("This operator connection has expired.");
        }

        var profiles = await _profiles.LoadAsync().ConfigureAwait(false);
        var desks = await _launcher.RunExclusiveAsync(() => _launcher.Workspaces.Workspaces
            .Where(desk => desk.Type == WorkspaceType.Sessions)
            .Select(desk => new OperatorSessionChoice(desk.Id, desk.Name)).ToArray()).ConfigureAwait(false);
        return new OperatorSessionCatalog(channel.SessionCapability, _sessionEpoch, desks,
            profiles.Select(profile => new OperatorSessionChoice(profile.Label, profile.Label)).ToArray());
    }

    private Task<OperatorSessionResult> _StartSessionAsync(_Channel channel, string requestId, string capability, string epoch, string deskId, string profileId)
    {
        if (!_CanManage(channel, capability) || epoch != _sessionEpoch || string.IsNullOrWhiteSpace(requestId) || requestId.Length > 128)
        {
            return Task.FromResult(new OperatorSessionResult(false, "This operator connection or request has expired."));
        }

        _Start start;
        lock (_gate)
        {
            var key = (channel.DeviceId, requestId);
            if (_starts.TryGetValue(key, out var previous))
            {
                if (previous.Desk != deskId || previous.Profile != profileId)
                {
                    return Task.FromResult(new OperatorSessionResult(false, "This request id already names a different session start."));
                }
                start = previous;
            }
            else
            {
                // Never evict a completed start: a delayed retry must not create another process.
                if (_starts.Count >= 4096)
                {
                    return Task.FromResult(new OperatorSessionResult(false, "The host's session-start request store is full."));
                }
                start = new _Start(deskId, profileId, new Lazy<Task<OperatorSessionResult>>(() => _LaunchAsync(channel, capability, deskId, profileId)));
                _starts.Add(key, start);
            }
        }

        return start.Result.Value;
    }

    private async Task<OperatorSessionResult> _LaunchAsync(_Channel channel, string capability, string deskId, string profileId)
    {
        try
        {
            if (_launcher is null || _profiles is null || !_CanManage(channel, capability))
            {
                return new OperatorSessionResult(false, "This operator connection has expired.");
            }

            var profiles = await _profiles.LoadAsync().ConfigureAwait(false);
            var profile = profiles.FirstOrDefault(item => item.Label == profileId);
            var validDesk = await _launcher.RunExclusiveAsync(() => _launcher.Workspaces.Workspaces
                .Any(desk => desk.Id == deskId && desk.Type == WorkspaceType.Sessions)).ConfigureAwait(false);
            if (profile is null || !validDesk || !_CanManage(channel, capability))
            {
                return new OperatorSessionResult(false, "The selected desk or profile is no longer available. Reload the catalog.");
            }

            var result = await _launcher.StartSessionAsync(new SessionLaunchRequest(deskId, profile, null, null, null,
                null, null, null, null, false)).ConfigureAwait(false);
            if (result is null || string.IsNullOrEmpty(result.PaneId) || result.FailureReason is not null || result.WorktreeDecision is not null)
            {
                return new OperatorSessionResult(false, result?.FailureReason ?? "The host could not start this session. Review the profile on the desktop.");
            }
            return new OperatorSessionResult(true, null, result.PaneId);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _logger.LogWarning("An operator session start failed: {Reason}", exception.GetType().Name);
            return new OperatorSessionResult(false, "The host could not start this session. Review the profile on the desktop.");
        }
    }

    private async Task<OperatorSessionResult> _StopSessionAsync(_Channel channel, string capability, string paneId)
    {
        if (!_CanManage(channel, capability) || _launcher is null || _sessions is null || paneId == "cockpit-assistant")
        {
            return new OperatorSessionResult(false, "This operator connection or session is unavailable.");
        }
        try
        {
            var session = _sessions.Find(paneId);
            if (session is null)
            {
                return new OperatorSessionResult(false, "This session has already closed.", paneId);
            }
            var valid = await _launcher.RunExclusiveAsync(() => !session.IsTerminal && !session.IsEmbedded && _launcher.Workspaces.Workspaces
                .Any(desk => desk.Type == WorkspaceType.Sessions && desk.Panes.Any(pane => pane.Id == paneId))).ConfigureAwait(false);
            if (!valid || !_CanManage(channel, capability))
            {
                return new OperatorSessionResult(false, "Only an agent session on a Sessions desk can be stopped.", paneId);
            }
            await _launcher.StopSessionAsync(paneId).ConfigureAwait(false);
            return new OperatorSessionResult(true, null, paneId);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _logger.LogWarning("An operator session stop failed: {Reason}", exception.GetType().Name);
            return new OperatorSessionResult(false, "The host could not stop this session.", paneId);
        }
    }

    private sealed record _Start(string Desk, string Profile, Lazy<Task<OperatorSessionResult>> Result);
}
