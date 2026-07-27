namespace Cockpit.App.Controls;

/// <summary>
/// Which of the two title bars <see cref="CockpitWindowChrome"/> gives a window. They differ in scale, not in
/// behaviour: both carry the name and the caption buttons, and both drag the window.
/// </summary>
internal enum CockpitTitleBar
{
    /// <summary>
    /// A dialog's own heading: the name at heading scale, optionally with a line under it saying what the dialog
    /// is for. It replaces the heading a dialog used to draw in its own content, so the name is stated once.
    /// </summary>
    Dialog,

    /// <summary>The app window's bar: one compact line, because what matters there is the window under it.</summary>
    Window,
}
