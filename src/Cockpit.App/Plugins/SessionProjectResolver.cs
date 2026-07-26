using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Sessions;

namespace Cockpit.App.Plugins;

/// <summary>
/// Turns a pane into the project its session belongs to (AC-320) — the one implementation of that lookup, shared by
/// everything that needs it: what a plugin contributes to a starting session, what a plugin reads off a project, and
/// what a delegated task inherits from the session that asked for it.
/// </summary>
/// <remarks>
/// <paramref name="services"/> rather than a constructor-injected <see cref="CockpitViewModel"/>: the view model owns
/// the sessions, so depending on it directly would close a cycle. Resolved lazily at call time, the same way
/// <see cref="SessionResourceResolver"/> reaches it.
/// </remarks>
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
