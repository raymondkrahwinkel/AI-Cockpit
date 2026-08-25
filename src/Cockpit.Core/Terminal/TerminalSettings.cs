namespace Cockpit.Core.Terminal;

// User-configurable TTY terminal-appearance settings, persisted under the `terminal` section of `cockpit.json`.
// Global across all TTY sessions (#40) — deliberately not per-profile or per-session — and applies only to the
// TTY renderer, not the SDK transcript view.
public sealed record TerminalSettings
{
    // Font-family fallback list fed straight into `TerminalControl.FontFamily`. "Cascadia Mono" is Windows-only,
    // hence the monospace fallback so the terminal doesn't drop to a proportional font on Linux/macOS.
    public string FontFamily { get; init; } = "Cascadia Mono, Consolas, monospace";

    // Terminal font size in points, fed into `TerminalControl.FontSize`. Clamped to 8-32 on save (see `MinFontSize`/`MaxFontSize`).
    public int FontSize { get; init; } = 13;

    // Lower bound enforced when saving `FontSize` — below this the TUI grid becomes unreadable.
    public const int MinFontSize = 8;

    // Upper bound enforced when saving `FontSize` — above this a typical terminal grid no longer fits a useful column count.
    public const int MaxFontSize = 32;

    // The shell a new terminal pane opens (#AC-25), as a `ShellDescriptor.Id` or an absolute path. Blank —
    // the default — means "the OS default" as detected by `ShellCatalog`; a value that no longer resolves
    // falls back to that same default rather than failing to open.
    public string Shell { get; init; } = string.Empty;
}
