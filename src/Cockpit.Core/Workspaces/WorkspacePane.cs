namespace Cockpit.Core.Workspaces;

/// <summary>
/// One placed pane in a workspace, as persisted: what it is, where it sits, and the minimum needed to
/// rebuild it after a restart. Deliberately thin — a widget's own configuration is <em>not</em> here but in
/// the plugin's per-instance storage keyed by <see cref="Id"/> (<c>IWidgetContext.Storage</c>), so the host
/// never has to know the shape of a plugin's config and <c>cockpit.json</c> never grows plugin blobs.
/// </summary>
/// <remarks>
/// For an <see cref="PaneKind.AiSession"/> pane specifically (AC-410): this record carries the operator's
/// <em>intention</em> — where the session was put, which profile and kind it was started with — not what the
/// running session actually did. What it actually did (the resolved worktree, the reported conversation id) lives
/// in <c>SessionStateRecord</c>/<c>session-state.jsonl</c> instead, keyed by the same pane id. The two can
/// legitimately disagree — a worktree relocates the working directory the session runs in — and a restore reads
/// both rather than trusting either alone.
/// </remarks>
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

    /// <summary>
    /// For <see cref="PaneKind.AiSession"/>: the session's display title at the moment it was persisted (AC-410) —
    /// carried here, not recomputed, so a restored pane does not come back as "&lt;profile&gt; - N" with a fresh
    /// number. Null for a pane kind that names itself another way.
    /// </summary>
    public string? Title { get; init; }

    /// <summary>
    /// For <see cref="PaneKind.AiSession"/>: whether <see cref="Title"/> was chosen by the operator rather than
    /// composed by the cockpit (AC-410) — the persisted mirror of <c>NewSessionResult.NameIsChosen</c>, so a
    /// restored session still tells a later name suggestion (#AC-310) apart from one somebody typed.
    /// </summary>
    public bool NameIsChosen { get; init; }

    /// <summary>
    /// For <see cref="PaneKind.AiSession"/>: which factory rebuilds this pane after a restart — an SDK chat panel
    /// or a TTY terminal panel (AC-410). Defaults to <see cref="PaneSessionKind.Sdk"/>, the same fallback
    /// <see cref="Kind"/> itself gets from an unparseable value, so a hand-edited or older <c>cockpit.json</c>
    /// degrades to a session kind rather than failing to load.
    /// </summary>
    public PaneSessionKind SessionKind { get; init; }

    /// <summary>For <see cref="PaneKind.AiSession"/>: the project this session works on (AC-410), or null for one belonging to none. Mirrors <c>NewSessionResult.ProjectId</c>.</summary>
    public string? ProjectId { get; init; }
}
