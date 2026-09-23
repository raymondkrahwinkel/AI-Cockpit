using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Sessions;

namespace Cockpit.Infrastructure.Sessions;

// AC-1374: moved from Cockpit.App.Services — reads the pane through the session registry (AC-1373) instead of
// CockpitViewModel; the UI-thread marshalling that lived here moved into the handle itself.
internal sealed class SessionLabelSink(ISessionRegistry sessions) : ISessionLabelSink, ISingletonService
{
    public Task<bool> SetStatuslineAsync(string paneId, string statusline) =>
        sessions.Find(paneId) is { } session ? session.SetStatuslineAsync(statusline) : Task.FromResult(false);

    public Task<bool> SuggestNameAsync(string paneId, string name) =>
        sessions.Find(paneId) is { } session ? session.SuggestNameAsync(name) : Task.FromResult(false);
}
