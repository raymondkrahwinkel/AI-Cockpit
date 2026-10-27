namespace Cockpit.Plugins.Abstractions.Workspaces;

/// <summary>
/// How <see cref="ICockpitUiHost.OpenWorkspaceAsync(string, WorkspaceOpenOptions)"/> opens a workspace: which instance of
/// the type, its tab title, and whether it is a preview the next preview replaces.
/// </summary>
/// <param name="InstanceKey">
/// What this instance shows (a file, say). A workspace of the same type with the same key gets focus instead of a new tab.
/// Null opens the type the way <see cref="ICockpitUiHost.OpenWorkspaceAsync(string)"/> does, unless <paramref name="Preview"/> is set.
/// </param>
/// <param name="Title">The tab title of a new workspace; null takes the type's title. The body can change it later.</param>
/// <param name="Preview">
/// Opens it as the preview tab: at most one per window, replaced in its place by the next preview, until it is pinned.
/// </param>
public sealed record WorkspaceOpenOptions(string? InstanceKey = null, string? Title = null, bool Preview = false);
