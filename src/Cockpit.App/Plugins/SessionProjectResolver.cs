using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Sessions;

namespace Cockpit.App.Plugins;

// AC-320: the one implementation of pane-to-project lookup, shared by every caller that needs it.
// Takes `services` rather than a constructor-injected `CockpitViewModel` and resolves it lazily
// at call time, since the view model owns the sessions and depending on it directly would cycle.
internal sealed class SessionProjectResolver(IServiceProvider services) : ISessionProjectResolver, ISingletonService
{
    public async Task<string?> ProjectIdOfAsync(string? paneId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(paneId) || services.GetService<CockpitViewModel>() is not { } cockpit)
        {
            return null;
        }

        // The lookup walks the on-screen session collections, so it happens on the UI thread; a caller may ask from
        // any. FindSession reaches embedded sessions too, so a run started inside a workspace is not a blind spot.
        return Dispatcher.UIThread.CheckAccess()
            ? cockpit.FindSession(paneId)?.ProjectId
            : await Dispatcher.UIThread.InvokeAsync(() => cockpit.FindSession(paneId)?.ProjectId);
    }
}
