using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Agents;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.Sessions;
using Microsoft.Extensions.Logging;

namespace Cockpit.Infrastructure.Sessions;

// AC-1449: a desktop pane's control is a host of its own, built from what the container has; one without a manager
// (the design-time graph) cannot launch.
public sealed class SessionControlFactory(
    ISessionManager? manager,
    IAgentTurnInboxDelivery? turnInboxDelivery = null,
    IProfileLoginChecker? loginChecker = null,
    ISharedUsageCache? sharedUsageCache = null,
    ISessionTranscriptStore? transcriptStore = null,
    SessionStateRecorder? stateRecorder = null,
    ILogger<SessionHost>? logger = null,
    TimeProvider? time = null) : ISessionControlFactory, ISingletonService
{
    public ISessionControl Create(Func<string> paneId) => CreateHost(paneId);

    // AC-1439: the same host for the backend launcher, so a headless session gets what a desktop pane's does.
    public SessionHost CreateHost(Func<string> paneId) => new(
        paneId, manager, time ?? TimeProvider.System, turnInboxDelivery, loginChecker, sharedUsageCache, logger, transcriptStore,
        stateRecorder);
}
