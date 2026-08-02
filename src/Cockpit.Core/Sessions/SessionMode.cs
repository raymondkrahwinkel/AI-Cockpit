namespace Cockpit.Core.Sessions;

// How a cockpit session drives its underlying `claude` process.
public enum SessionMode
{
    // Default. Headless, persistent stream-json process rendered as the cockpit's own chat UI
    // (`SessionView`) — the production path.
    Sdk,

    // Experiment (#9). The real interactive `claude` TUI hosted inside a ConPTY and rendered
    // in a terminal panel, so the literal terminal experience runs in-cockpit.
    Tty,
}
