using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.Infrastructure.Projects;

// AC-1374: moved from Cockpit.App.Plugins, kept out of Core — its signature needs ISharedProjectSource, a
// Plugins.Abstractions type Core carries no reference to at all. Infrastructure already does, so both the
// contract and its implementation live here.
/// <summary>
/// Holds the shared-project sources plugins register (<c>ICockpitHost.AddSharedProjectSource</c>, AC-245), so the
/// Projects workspace and the assistant's read gateway list what they offer without depending on the contributing
/// plugins. Same shape as App's <c>IProjectMemorySourceRegistry</c>.
/// </summary>
public interface ISharedProjectSourceRegistry
{
    /// <summary>
    /// Records a source. A key that is already registered is refused, first one wins.
    /// </summary>
    /// <returns>
    /// False when another plugin already contributes this key — the caller says so; nothing throws.
    /// </returns>
    bool Register(ISharedProjectSource source);

    /// <summary>
    /// Withdraws the source registered under <paramref name="key"/>. A no-op when nothing is registered under it.
    /// </summary>
    void Remove(string key);

    /// <summary>
    /// Every source registered so far, in registration order.
    /// </summary>
    IReadOnlyList<ISharedProjectSource> Sources { get; }

    /// <summary>
    /// Raised right after a source is added (AC-762) — plugins register in a later startup phase than the window
    /// that kicks off the first <c>ProjectsViewModel.LoadSharedProjectsAsync</c>, so a source arriving after that
    /// race has already been lost is otherwise never retried until the operator opens Manage projects.
    /// </summary>
    event Action<ISharedProjectSource>? Registered;
}
