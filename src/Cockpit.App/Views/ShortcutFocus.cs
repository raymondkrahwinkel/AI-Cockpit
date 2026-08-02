namespace Cockpit.App.Views;

// Where the keyboard is when a shortcut's gesture matches — the only thing the dispatch gate needs
// to know about the focus, and one value rather than a pair of flags so a call site cannot state it backwards.
public enum ShortcutFocus
{
    // Focus is somewhere that does not swallow typing: a list, a button, the window itself.
    Elsewhere,

    // A text field has the keyboard, so a keystroke is most likely a character or caret movement.
    TextBox,

    // An embedded terminal has the keyboard, so a keystroke belongs to the shell or the TUI in it.
    Terminal,
}
