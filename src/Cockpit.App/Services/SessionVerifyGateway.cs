using Avalonia.Threading;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Verify;

namespace Cockpit.App.Services;

// AC-1013: AC-86's host-side IVerifySessionGateway; resolves a session by pane id from CockpitViewModel,
// reads its working directory for the verify tool's runner, and marshals the result onto the UI thread —
// the per-kind send-vs-TTY "how" stays in SessionPanelViewModel.FeedVerifyResultAsync.
internal sealed class SessionVerifyGateway(CockpitViewModel cockpit) : IVerifySessionGateway, ISingletonService
{
    public string? GetWorkingDirectory(string paneId) => _Find(paneId)?.WorkingDirectory;

    public async Task<bool> FeedResultAsync(string paneId, string caption, byte[] screenshotPng, CancellationToken cancellationToken = default)
    {
        if (_Find(paneId) is not { } session)
        {
            return false;
        }

        // AC-1013 (AC-577): no CheckAccess() fast path — all callers arrive off the UI thread, so it would
        // only ever be exercised by a test, a false-green. Not for a dispatcher-less process; that hangs, not fails.
        return await Dispatcher.UIThread.InvokeAsync(() => session.FeedVerifyResultAsync(caption, screenshotPng)).ConfigureAwait(false);
    }

    private SessionPanelViewModel? _Find(string paneId) =>
        cockpit.Sessions.FirstOrDefault(session => string.Equals(session.PaneId, paneId, StringComparison.Ordinal));
}
