using Microsoft.Extensions.DependencyInjection;
using Cockpit.Core.Abstractions;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.Workspaces;

namespace Cockpit.App.Plugins;

/// <summary>
/// Holds the workspace types plugins register (<c>ICockpitHost.AddWorkspaceType</c>), so the tab strip's "+"
/// menu can offer them and a saved desk of a plugin type can rebuild its body. A registry of its own — the same
/// shape as <see cref="IWidgetRegistry"/> — rather than a collection on a view model, so the menu reads it
/// without the two depending on each other. Empty is the normal case until a workspace-providing plugin is
/// installed.
/// </summary>
public interface IWorkspaceTypeRegistry
{
    /// <summary>
    /// Records a workspace type along with what its owning plugin brought: its storage and the observe surface. A
    /// type id that is already registered is refused, first one wins.
    /// </summary>
    /// <returns>False when another plugin already contributes this type id — the caller says so; nothing throws.</returns>
    bool Register(WorkspaceTypeRegistration registration, IPluginStorage pluginStorage, ICockpitSessionObserver sessions);

    /// <summary>
    /// Raised when a plugin contributes a workspace type. Plugins initialize after the cockpit's view models are
    /// built, so anything reading <see cref="WorkspaceTypes"/> would otherwise read an empty list once, at startup,
    /// and never hear about the types that arrived a moment later — the same reason the widget registry raises this.
    /// </summary>
    event EventHandler? Changed;

    /// <summary>Every workspace type registered so far, in registration order — what the "+" menu lists.</summary>
    IReadOnlyList<WorkspaceTypeRegistration> WorkspaceTypes { get; }

    /// <summary>
    /// Builds the registration and context for a workspace of <paramref name="typeId"/>, or null when no plugin
    /// contributes that type — an uninstalled or disabled plugin leaves its workspaces behind on the strip, and
    /// that body has to be a placeholder rather than a crash.
    /// </summary>
    (WorkspaceTypeRegistration Registration, IWorkspaceContext Context)? CreateBody(string typeId, string workspaceId);

    /// <summary>Whether a plugin here contributes <paramref name="typeId"/>.</summary>
    bool IsRegistered(string typeId);
}

internal sealed class WorkspaceTypeRegistry(IServiceProvider services) : IWorkspaceTypeRegistry, ISingletonService
{
    private readonly List<RegisteredWorkspaceType> _types = [];

    public event EventHandler? Changed;

    public IReadOnlyList<WorkspaceTypeRegistration> WorkspaceTypes => [.. _types.Select(type => type.Registration)];

    /// <summary>
    /// First registration of a type id wins, and a later one is refused rather than added beside it — the same
    /// rule the widget registry keeps, and for the same reason: two entries with one id put the type in the "+"
    /// menu twice and leave <see cref="CreateBody"/> resolving to whichever plugin loaded first.
    /// </summary>
    public bool Register(WorkspaceTypeRegistration registration, IPluginStorage pluginStorage, ICockpitSessionObserver sessions)
    {
        if (IsRegistered(registration.Id))
        {
            return false;
        }

        _types.Add(new RegisteredWorkspaceType(registration, pluginStorage, sessions));
        Changed?.Invoke(this, EventArgs.Empty);

        return true;
    }

    public bool IsRegistered(string typeId) => _types.Any(type => type.Registration.Id == typeId);

    public (WorkspaceTypeRegistration Registration, IWorkspaceContext Context)? CreateBody(string typeId, string workspaceId)
    {
        if (_types.FirstOrDefault(type => type.Registration.Id == typeId) is not { } registered)
        {
            return null;
        }

        // Resolved here rather than injected: the shell view model that implements IEmbeddedSessionHost is built
        // after this singleton, and a workspace body is created long after startup — so there is no construction
        // cycle to break, only a service to reach when a body actually asks to embed a session (Code.md §2 —
        // service location is for orchestrators that lazily resolve to break a cycle).
        var embeddedSessions = services.GetService<IEmbeddedSessionHost>();
        var context = new WorkspaceContext(workspaceId, registered.PluginStorage, registered.Sessions, embeddedSessions);
        return (registered.Registration, context);
    }
}
