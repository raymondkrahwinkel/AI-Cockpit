namespace Cockpit.Core.Terminal;

// User-configurable TTY terminal-appearance settings, persisted under the `terminal` section of
// `cockpit.json` (same store pattern as layout/transcript-display). Global across all TTY
// sessions (#40) — deliberately not per-profile or per-session — and applies only to the TTY renderer
// (`Exclr8.Terminal.TerminalControl`), not the SDK transcript view, which renders its own chat UI
// rather than a terminal grid.
public sealed record TerminalSettings
{
    // Font-family fallback list fed straight into `TerminalControl.FontFamily` (and from there
    // into Avalonia's `global::Avalonia.Media.Typeface` constructor), so both a single
    // family name and a comma-separated fallback list work. "Cascadia Mono" is Windows-only, hence the
    // monospace fallback so the terminal doesn't drop to a proportional font on Linux/macOS.
    public string FontFamily { get; init; } = "Cascadia Mono, Consolas, monospace";

    // Terminal font size in points, fed into `TerminalControl.FontSize`. Clamped to 8-32 on save (see `MinFontSize`/`MaxFontSize`).
    public int FontSize { get; init; } = 13;

    // Lower bound enforced when saving `FontSize` — below this the TUI grid becomes unreadable.
    public const int MinFontSize = 8;

    // Upper bound enforced when saving `FontSize` — above this a typical terminal grid no longer fits a useful column count.
    public const int MaxFontSize = 32;

    // The shell a new terminal pane opens (#AC-25), as a `ShellDescriptor.Id` ("pwsh", "bash", …) or an
    // absolute path. Blank — the default — means "the OS default": the first shell `ShellCatalog` detects,
    // so a fresh install opens a sensible shell without any configuration. A value that no longer resolves on this
    // machine falls back to that same OS default rather than failing to open.
    public string Shell { get; init; } = string.Empty;
}
