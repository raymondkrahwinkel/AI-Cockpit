using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Verify;

namespace Cockpit.Infrastructure.Verify;

// AC-1374: moved from Cockpit.App.Services — reads the pane through the session registry (AC-1373) instead of
// CockpitViewModel; the UI-thread marshalling that lived here moved into the handle itself.
internal sealed class SessionVerifyGateway(ISessionRegistry sessions) : IVerifySessionGateway, ISingletonService
{
    // Grid panes only, like the App implementation this replaces (`cockpit.Sessions`, not `AllSessions`) — an
    // embedded pane (an Autopilot step) has no verify runner of its own to hand a render back to.
    public string? GetWorkingDirectory(string paneId) =>
        sessions.Find(paneId) is { IsEmbedded: false } session ? session.WorkingDirectory : null;

    public Task<bool> FeedResultAsync(string paneId, string caption, byte[] screenshotPng, CancellationToken cancellationToken = default) =>
        sessions.Find(paneId) is { IsEmbedded: false } session ? session.FeedVerifyResultAsync(caption, screenshotPng) : Task.FromResult(false);
}
