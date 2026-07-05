namespace Cockpit.Core.Claude;

/// <summary>
/// How a cockpit session drives its underlying <c>claude</c> process.
/// </summary>
public enum SessionMode
{
    /// <summary>
    /// Default. Headless, persistent stream-json process rendered as the cockpit's own chat UI
    /// (<c>ClaudeSessionView</c>) — the production path.
    /// </summary>
    Sdk,

    /// <summary>
    /// Experiment (#9). The real interactive <c>claude</c> TUI hosted inside a ConPTY and rendered
    /// in a terminal panel, so the literal terminal experience runs in-cockpit.
    /// </summary>
    Tty,
}
