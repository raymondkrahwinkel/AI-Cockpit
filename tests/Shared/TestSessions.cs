using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Agents;
using Cockpit.Core.Abstractions.Events;
using Cockpit.Core.Abstractions.Mentions;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Usage;
using Cockpit.Infrastructure.Sessions;
using Microsoft.Extensions.Logging;

namespace Cockpit.Tests.Shared;

// AC-1449: a pane over a session manager, as the pane's constructor took one before its control came from a factory;
// the parameters keep that constructor's names and order, so a test reads as it did.
internal static class TestSessions
{
    public static SessionViewModel Pane(
        ISessionManager sessionManager,
        IVoicePushToTalkService? voicePushToTalk = null,
        IVoiceSettingsStore? voiceSettingsStore = null,
        IVoicePlaybackQueue? voicePlaybackQueue = null,
        IOpenMicState? openMicState = null,
        IUsageHistory? usageHistory = null,
        IAgentTurnInboxDelivery? turnInboxDelivery = null,
        SessionStateRecorder? sessionStateRecorder = null,
        ISessionTranscriptStore? transcriptStore = null,
        ISessionProviderNames? providerNames = null,
        IProviderUsageSignals? usageSignals = null,
        VoiceOverlayCoordinator? voiceOverlay = null,
        IProfileLoginChecker? loginChecker = null,
        ISessionLoginFlows? loginFlows = null,
        IMentionFileSource? mentionFileSource = null,
        ISharedUsageCache? sharedUsageCache = null,
        ISessionTranscriptReader? transcriptReader = null,
        ILogger<SessionViewModel>? logger = null,
        TimeProvider? timeProvider = null,
        IBackendEventLog? eventLog = null) =>
        new(
            new SessionControlFactory(
                sessionManager, turnInboxDelivery, loginChecker, sharedUsageCache, transcriptStore, sessionStateRecorder,
                time: timeProvider),
            voicePushToTalk, voiceSettingsStore, voicePlaybackQueue, openMicState, usageHistory, turnInboxDelivery,
            providerNames, usageSignals, voiceOverlay, loginChecker, loginFlows, mentionFileSource, transcriptReader, logger,
            eventLog);
}
