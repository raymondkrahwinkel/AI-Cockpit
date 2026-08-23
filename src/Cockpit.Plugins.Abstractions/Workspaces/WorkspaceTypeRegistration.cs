using Avalonia.Controls;
using Material.Icons;

namespace Cockpit.Plugins.Abstractions.Workspaces;

/// <summary>
/// A workspace type a plugin contributes (<see cref="ICockpitHost.AddWorkspaceType"/>) — the full-surface
/// counterpart of a widget: the host draws the tab and the frame, and <see cref="CreateBody"/> draws everything
/// inside it.
/// </summary>
/// <remarks>
/// Where <see cref="WidgetRegistration"/> fills one cell of the host's dashboard grid, a workspace type owns its
/// whole body. The tab strip's "+" menu offers every registered type.
/// </remarks>
/// <param name="Id">
/// A stable, unique id for the workspace <em>type</em>, namespaced by the plugin (e.g. "autopilot.run").
/// Persisted with each workspace of this type so a saved desk rebuilds after a restart; changing it orphans
/// existing workspaces — they render as a placeholder until the id comes back — so treat it as an API surface.
/// </param>
/// <param name="Title">
/// The type's display name, shown in the "+" menu and as a new workspace's default tab label.
/// </param>
/// <param name="CreateBody">
/// Builds the whole workspace body, on the UI thread, handed that workspace's own <see cref="IWorkspaceContext"/>
/// (per-workspace storage, the session-observe surface, the session-embedding seam, a refresh signal). Invoked
/// once per workspace instance; the body owns its own layout and lifetime from there.
/// </param>
public sealed record WorkspaceTypeRegistration(string Id, string Title, Func<IWorkspaceContext, Control> CreateBody)
{
    /// <summary>
    /// A short glyph shown in the "+" menu and on the tab when <see cref="IconKind"/> is null. Empty by default — every bundled workspace type sets <see cref="IconKind"/> instead; a plugin may still put an emoji or letter here.
    /// </summary>
    public string Icon { get; init; } = string.Empty;

    /// <summary>
    /// A bundled vector icon for the "+" menu and the tab, preferred over <see cref="Icon"/> when set, so the
    /// type reads as part of the theme instead of an emoji the host renders in the machine's own font. Null keeps
    /// the <see cref="Icon"/> string.
    /// </summary>
    public MaterialIconKind? IconKind { get; init; }

    /// <summary>
    /// One line describing the workspace type for the "+" menu. Empty by default.
    /// </summary>
    public string Description { get; init; } = string.Empty;
}
