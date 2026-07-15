namespace Cockpit.Core.Workspaces;

/// <summary>
/// One placed pane in a workspace, as persisted: what it is, where it sits, and the minimum needed to
/// rebuild it after a restart. Deliberately thin — a widget's own configuration is <em>not</em> here but in
/// the plugin's per-instance storage keyed by <see cref="Id"/> (<c>IWidgetContext.Storage</c>), so the host
/// never has to know the shape of a plugin's config and <c>cockpit.json</c> never grows plugin blobs.
/// </summary>
/// <param name="Id">This instance's stable id — the widget's <c>InstanceId</c>, and the key its config is stored under.</param>
/// <param name="Kind">What the pane holds; must be accepted by the owning workspace's type (<see cref="WorkspaceTypeRules"/>).</param>
public sealed record WorkspacePane(string Id, PaneKind Kind)
{
    /// <summary>Where the pane sits in the grid.</summary>
    public GridCell Cell { get; init; } = new(0, 0);

    /// <summary>For <see cref="PaneKind.Widget"/>: the widget <em>type</em> id it was created from (<c>WidgetRegistration.Id</c>), e.g. "system-monitor.usage".</summary>
    public string? WidgetId { get; init; }

    /// <summary>For <see cref="PaneKind.AiSession"/>: the profile the session runs under.</summary>
    public string? ProfileId { get; init; }

    /// <summary>For <see cref="PaneKind.Terminal"/>: the shell command to launch (e.g. "pwsh", "bash"); null = the OS default shell.</summary>
    public string? Shell { get; init; }

    /// <summary>For <see cref="PaneKind.AiSession"/>/<see cref="PaneKind.Terminal"/>: the working directory to start in; null = the app's own.</summary>
    public string? WorkingDirectory { get; init; }
}
