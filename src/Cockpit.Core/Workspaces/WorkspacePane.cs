namespace Cockpit.Core.Workspaces;

// One placed pane in a workspace, as persisted: what it is, where it sits, and the minimum to rebuild it.
// AC-1013: trimmed — deliberately thin, widget config lives in plugin storage not here; for AiSession (AC-410)
// this record carries operator *intention* only, not actual outcome — see ticket for full rationale.
public sealed record WorkspacePane(string Id, PaneKind Kind)
{
    // Where the pane sits in the grid.
    public GridCell Cell { get; init; } = new(0, 0);

    // For `PaneKind.Widget`: the widget *type* id it was created from (`WidgetRegistration.Id`), e.g. "system-monitor.usage".
    public string? WidgetId { get; init; }

    // For `PaneKind.AiSession`: the profile the session runs under.
    public string? ProfileId { get; init; }

    // For `PaneKind.Terminal`: the shell command to launch (e.g. "pwsh", "bash"); null = the OS default shell.
    public string? Shell { get; init; }

    // For `PaneKind.AiSession`/`PaneKind.Terminal`: the working directory to start in; null = the app's own.
    public string? WorkingDirectory { get; init; }

    // For `PaneKind.AiSession`: the session's display title at the moment it was persisted (AC-410) —
    // carried here, not recomputed, so a restored pane does not come back as "&lt;profile&gt; - N" with a fresh
    // number. Null for a pane kind that names itself another way.
    public string? Title { get; init; }

    // For `PaneKind.AiSession`: whether `Title` was chosen by the operator rather than
    // composed by the cockpit (AC-410) — the persisted mirror of `NewSessionResult.NameIsChosen`, so a
    // restored session still tells a later name suggestion (#AC-310) apart from one somebody typed.
    public bool NameIsChosen { get; init; }

    // For `PaneKind.AiSession`: which factory rebuilds this pane after a restart — SDK chat panel or TTY
    // terminal (AC-410). Defaults to `PaneSessionKind.Sdk`, the same fallback `Kind` gets from an unparseable
    // value, so a hand-edited or older `cockpit.json` degrades to a session kind rather than failing to load.
    public PaneSessionKind SessionKind { get; init; }

    // For `PaneKind.AiSession`: the project this session works on (AC-410), or null for one belonging to none. Mirrors `NewSessionResult.ProjectId`.
    public string? ProjectId { get; init; }
}
