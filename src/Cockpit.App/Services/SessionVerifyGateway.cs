using Avalonia.Threading;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Verify;

namespace Cockpit.App.Services;

// Host-side `IVerifySessionGateway` (AC-86) over the running session panels: it resolves a session by
// its pane id from the root `CockpitViewModel`, reads where that session runs so the verify tool can
// find the project's runner, and feeds a result back into it on the UI thread the panels live on. The per-kind
// "how" — an SDK send versus a TTY inject-and-Enter — is the panel's own
// `SessionPanelViewModel.FeedVerifyResultAsync`, so this only routes to the right panel and marshals.
internal sealed class SessionVerifyGateway(CockpitViewModel cockpit) : IVerifySessionGateway, ISingletonService
{
    public string? GetWorkingDirectory(string paneId) => _Find(paneId)?.WorkingDirectory;

    public async Task<bool> FeedResultAsync(string paneId, string caption, byte[] screenshotPng, CancellationToken cancellationToken = default)
    {
        if (_Find(paneId) is not { } session)
        {
            return false;
        }

        // AC-577, no fast path — deliberately. Every caller of this gateway arrives off the UI thread (it answers
        // an MCP tool call), so a CheckAccess() branch would never be taken in production and would exist only to
        // be taken by a test, which is the false-green this ticket is about. The trade written down: this type
        // must not be constructed in a process without a dispatcher loop, or the call hangs rather than fails.
        return await Dispatcher.UIThread.InvokeAsync(() => session.FeedVerifyResultAsync(caption, screenshotPng)).ConfigureAwait(false);
    }

    private SessionPanelViewModel? _Find(string paneId) =>
        cockpit.Sessions.FirstOrDefault(session => string.Equals(session.PaneId, paneId, StringComparison.Ordinal));
}
