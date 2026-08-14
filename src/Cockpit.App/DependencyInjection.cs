using Microsoft.Extensions.DependencyInjection;
using Cockpit.App.ViewModels;

namespace Cockpit.App;

public static class DependencyInjection
{
    // The factory delegates CockpitViewModel mints panes with, so it can open a session (and, transitively, its own
    // ISessionDriver/CLI process) per "New session" click without holding an injected IServiceProvider itself
    // (service-locator anti-pattern — Code.md §2).
    public static IServiceCollection AddSessionPanes(this IServiceCollection services)
    {
        services.AddTransient<Func<SessionViewModel>>(provider => () => ResolveOwnedPane<SessionViewModel>(provider));
        services.AddTransient<Func<TtyViewModel>>(provider => () => ResolveOwnedPane<TtyViewModel>(provider));

        return services;
    }

    // A pane gets a scope of its own rather than coming out of the root container. Microsoft.DI holds on to every
    // IAsyncDisposable a container hands out until that container is disposed — for the root, app exit — so panes
    // resolved from it were never collected, and a closed session kept its whole transcript, images and all, for
    // the run (AC-787). The scope goes with the pane, which disposes it along with itself.
    private static T ResolveOwnedPane<T>(IServiceProvider provider) where T : SessionPanelViewModel
    {
        var scope = provider.CreateAsyncScope();
        var pane = scope.ServiceProvider.GetRequiredService<T>();
        pane.OwnLifetimeScope(scope);

        return pane;
    }
}
