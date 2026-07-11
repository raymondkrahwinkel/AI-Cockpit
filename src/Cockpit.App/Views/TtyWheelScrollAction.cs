namespace Cockpit.App.Views;

/// <summary>The action <see cref="TtyWheelScrollGate"/> picked for a mouse-wheel notch over the TTY (#56/#57).</summary>
public enum TtyWheelScrollAction
{
    /// <summary>Alternate screen, no mouse tracking requested (#56): forward the notch as an Up/Down arrow-key press — Exclr8.Terminal's alternate screen keeps no scrollback of its own.</summary>
    ForwardArrowKeys,

    /// <summary>Primary/inline screen (#57): scroll Exclr8.Terminal's own primary-screen scrollback directly instead of relying on the notch falling through unhandled to TerminalControl's own wheel handling.</summary>
    NativeScroll,

    /// <summary>Alternate screen with mouse tracking requested: leave the event alone — TerminalControl's own SGR-mouse-report path already covers it.</summary>
    PassThrough,
}
