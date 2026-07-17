namespace Cockpit.Core.Terminal;

/// <summary>
/// User-configurable TTY terminal-appearance settings, persisted under the <c>terminal</c> section of
/// <c>cockpit.json</c> (same store pattern as layout/transcript-display). Global across all TTY
/// sessions (#40) — deliberately not per-profile or per-session — and applies only to the TTY renderer
/// (<c>Exclr8.Terminal.TerminalControl</c>), not the SDK transcript view, which renders its own chat UI
/// rather than a terminal grid.
/// </summary>
public sealed record TerminalSettings
{
    /// <summary>
    /// Font-family fallback list fed straight into <c>TerminalControl.FontFamily</c> (and from there
    /// into Avalonia's <see cref="global::Avalonia.Media.Typeface"/> constructor), so both a single
    /// family name and a comma-separated fallback list work. "Cascadia Mono" is Windows-only, hence the
    /// monospace fallback so the terminal doesn't drop to a proportional font on Linux/macOS.
    /// </summary>
    public string FontFamily { get; init; } = "Cascadia Mono, Consolas, monospace";

    /// <summary>Terminal font size in points, fed into <c>TerminalControl.FontSize</c>. Clamped to 8-32 on save (see <see cref="MinFontSize"/>/<see cref="MaxFontSize"/>).</summary>
    public int FontSize { get; init; } = 13;

    /// <summary>Lower bound enforced when saving <see cref="FontSize"/> — below this the TUI grid becomes unreadable.</summary>
    public const int MinFontSize = 8;

    /// <summary>Upper bound enforced when saving <see cref="FontSize"/> — above this a typical terminal grid no longer fits a useful column count.</summary>
    public const int MaxFontSize = 32;

    /// <summary>
    /// The shell a new terminal pane opens (#AC-25), as a <see cref="ShellDescriptor.Id"/> ("pwsh", "bash", …) or an
    /// absolute path. Blank — the default — means "the OS default": the first shell <see cref="ShellCatalog"/> detects,
    /// so a fresh install opens a sensible shell without any configuration. A value that no longer resolves on this
    /// machine falls back to that same OS default rather than failing to open.
    /// </summary>
    public string Shell { get; init; } = string.Empty;
}
