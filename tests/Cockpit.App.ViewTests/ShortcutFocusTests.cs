using Avalonia.Controls;
using Cockpit.App.Views;
using Exclr8.Terminal;

namespace Cockpit.App.ViewTests;

/// <summary>
/// Which of the three answers the dispatch gate gets decides whether a keystroke reaches the shortcut, the
/// shell or the caret — so getting the classification backwards is not a shortcut that misfires but a keyboard
/// that behaves inside out. It needs a platform: constructing any of these controls asks for one.
/// <para>
/// One branch is not covered and cannot be: the walk up the visual tree only differs from "is the focused
/// element the terminal" once <c>TerminalControl</c> has focusable children, and it is a leaf control, so
/// there is no way to build the case. It is kept because the day it grows a template is the day the answer
/// would silently change.
/// </para>
/// </summary>
[Collection("avalonia")]
public class ShortcutFocusTests
{
    [Fact]
    public void AFocusedTextBox_IsATextBox() => HeadlessAvalonia.Run(() =>
    {
        Assert.Equal(ShortcutFocus.TextBox, ShortcutDispatchGate.FocusOf(new TextBox()));
    });

    [Fact]
    public void AFocusedTerminal_IsATerminal() => HeadlessAvalonia.Run(() =>
    {
        Assert.Equal(ShortcutFocus.Terminal, ShortcutDispatchGate.FocusOf(new TerminalControl()));
    });

    [Fact]
    public void AButtonInAnOrdinaryPanel_IsElsewhere() => HeadlessAvalonia.Run(() =>
    {
        var button = new Button();
        _ = new Window { Content = new StackPanel { Children = { button } }, Width = 400, Height = 300 };

        Assert.Equal(ShortcutFocus.Elsewhere, ShortcutDispatchGate.FocusOf(button));
    });

    [Fact]
    public void NothingFocused_IsElsewhere() => HeadlessAvalonia.Run(() =>
    {
        Assert.Equal(ShortcutFocus.Elsewhere, ShortcutDispatchGate.FocusOf(null));
    });
}
