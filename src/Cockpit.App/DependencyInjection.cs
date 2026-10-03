using Microsoft.Extensions.DependencyInjection;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.App.Composition;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Events;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Infrastructure.BackendApi;

namespace Cockpit.App;

public static class DependencyInjection
{
    public static IServiceCollection AddRemoteSessionBackend(
        this IServiceCollection services,
        RemoteBackend backend,
        BackendApiClient client)
    {
        services.AddSingleton(client);
        services.AddSingleton(backend);
        services.AddSingleton<ISessionLauncher>(backend);
        services.AddSingleton<ISessionRegistry>(backend);
        services.AddSingleton<IBackendEventLog>(backend);
        services.AddSingleton<IProviderUsageSignals>(new RemoteProviderUsageSignals(backend));
        services.AddSingleton<ISessionLoginFlows>(new RemoteSessionLoginFlows(backend, client));
        return services;
    }

    // The factory delegates CockpitViewModel mints panes with, so it can open a session (and, transitively, its own
    // ISessionDriver/CLI process) per "New session" click without holding an injected IServiceProvider itself
    // (service-locator anti-pattern — Code.md §2).
    public static IServiceCollection AddSessionPanes(this IServiceCollection services)
    {
        services.AddTransient<Func<SessionViewModel>>(provider => () => ResolveOwnedPane<SessionViewModel>(provider));
        services.AddTransient<Func<TtyViewModel>>(provider => () => ResolveOwnedPane<TtyViewModel>(provider));

        // AC-1450: a pane over the control the desktop made and registered before it; the pane builds on that one.
        services.AddTransient<Func<ISessionControl, SessionViewModel>>(provider => control =>
        {
            var scope = provider.CreateAsyncScope();
            var pane = ActivatorUtilities.CreateInstance<SessionViewModel>(scope.ServiceProvider, new HandedControl(control));
            pane.OwnLifetimeScope(scope);
            return pane;
        });

        // AC-1439: the desktop's half of the backend's one launcher. The cockpit is reached when first asked, and the
        // cockpit reaches the launcher the same way, since each starts through the other.
        services.AddSingleton(provider => new DesktopSessionSeams(provider.GetRequiredService<CockpitViewModel>));
        services.AddSingleton<ISessionDesks>(provider => provider.GetRequiredService<DesktopSessionSeams>());
        services.AddSingleton<ISessionHosting>(provider => provider.GetRequiredService<DesktopSessionSeams>());
        services.AddSingleton<ISessionStartObserver>(provider => provider.GetRequiredService<DesktopSessionSeams>());
        services.AddSingleton<IAssistantSessionFactory>(provider => provider.GetRequiredService<DesktopSessionSeams>());
        services.AddTransient<Func<ISessionLauncher>>(provider => provider.GetRequiredService<ISessionLauncher>);

        return services;
    }

    // A pane gets a scope of its own rather than coming out of the root container: Microsoft.DI holds every
    // IAsyncDisposable a container hands out until that container is disposed — app exit, for the root — so a closed
    // pane kept its whole transcript for the run (AC-787). The scope goes with the pane and is disposed with it.
    private static T ResolveOwnedPane<T>(IServiceProvider provider) where T : SessionPanelViewModel
    {
        var scope = provider.CreateAsyncScope();
        var pane = scope.ServiceProvider.GetRequiredService<T>();
        pane.OwnLifetimeScope(scope);

        return pane;
    }

    private sealed class HandedControl(ISessionControl control) : ISessionControlFactory
    {
        public ISessionControl Create(Func<string> paneId) => control;
    }
}
