using Avalonia.Controls;
using Material.Icons;

namespace Cockpit.Plugins.Abstractions.UI.Onboarding;

/// <summary>
/// A way to begin that a plugin offers on the first-run wizard's "How do you want to start?" step
/// (<see cref="ICockpitUiHost.AddFirstStartRoute"/>), next to the host's own "Start fresh" and "Restore a backup file".
/// </summary>
/// <remarks>
/// The wizard shows the route as one choice; picking it hangs <see cref="CreateContent"/> under the choices. The route
/// does its own work in that content. It can only say whether it settles what kind of work this is for, so the wizard
/// strikes that step through and leaves it out of "Step n of N". The provider step is never skipped, because signing in to a
/// provider is local. The route never picks a slot of its own.
/// </remarks>
/// <param name="Id">
/// A stable, unique id for the route, namespaced by the plugin (e.g. "my-plugin.sign-in"). A second route with the same
/// id is ignored.
/// </param>
/// <param name="Title">
/// The choice's name, e.g. "Sign in to a Depot".
/// </param>
/// <param name="Description">
/// One line under the title saying what this way of starting does.
/// </param>
/// <param name="CreateContent">
/// Builds the content shown once the route is picked, on the UI thread, once per wizard.
/// </param>
public sealed record FirstStartRoute(string Id, string Title, string Description, Func<Control> CreateContent)
{
    /// <summary>
    /// A bundled vector icon for the choice. Null shows a generic plugin icon.
    /// </summary>
    public MaterialIconKind? IconKind { get; init; }

    /// <summary>
    /// Whether picking this route settles "What you work on", so the wizard skips that step. False by default.
    /// </summary>
    public bool AnswersWorkKind { get; init; }
}
