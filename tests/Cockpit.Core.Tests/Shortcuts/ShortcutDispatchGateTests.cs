using Avalonia.Input;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;

namespace Cockpit.Core.Tests.Shortcuts;

/// <summary>
/// The ways a shortcut can survive — or not — the operator typing. This rule used to sit in
/// <c>CockpitView</c>'s code-behind where nothing could reach it, so every branch below was previously only
/// reasoned about; a wrong answer here either swallows a keystroke the shell or the text box needed, or drops
/// a shortcut on the floor exactly when it is wanted.
/// </summary>
public class ShortcutDispatchGateTests
{
    private static readonly ShortcutBinding OneModifier = new("Ctrl+O", "Options", () => { });
    private static readonly ShortcutBinding Palette = new("Ctrl+K", "Command palette", () => { }, AlwaysActive: true);
    private static readonly ShortcutBinding SessionSwitch =
        new("Ctrl+Shift+Down", "Next session", () => { }, ActiveInTerminal: true);

    // Ctrl+N is the flag's only interesting shape: one modifier, so nothing carries it past a focused terminal
    // except ActiveInTerminal itself. Asserting the flag on a two-modifier gesture proves nothing, because the
    // gate has already said yes before it looks.
    private static readonly ShortcutBinding NewSession =
        new("Ctrl+N", "New session", () => { }, ActiveInTerminal: true);

    [Fact]
    public void ASingleModifierBinding_StandsDownInsideATextBox()
    {
        Assert.False(Live(OneModifier, ShortcutFocus.TextBox));
    }

    [Fact]
    public void ASingleModifierBinding_StandsDownOverAFocusedTerminal()
    {
        Assert.False(Live(OneModifier, ShortcutFocus.Terminal));
    }

    [Fact]
    public void ASingleModifierBinding_FiresWhenNothingIsBeingTypedInto()
    {
        Assert.True(Live(OneModifier, ShortcutFocus.Elsewhere));
    }

    [Fact]
    public void TheCommandPalette_FiresEvenWhileTyping_BecauseItIsTheWayBackToEverythingElse()
    {
        Assert.True(Live(Palette, ShortcutFocus.TextBox));
        Assert.True(Live(Palette, ShortcutFocus.Terminal));
    }

    [Fact]
    public void ATerminalAllowedBinding_FiresOverTheTerminalOnItsFlagAlone()
    {
        Assert.True(Live(NewSession, ShortcutFocus.Terminal));
    }

    [Fact]
    public void ATerminalAllowedBinding_StillStandsDownInATextBox()
    {
        Assert.False(Live(NewSession, ShortcutFocus.TextBox));
    }

    [Fact]
    public void ATwoModifierGesture_OutranksEvenATextBox()
    {
        // Which is why the chord a default is given matters: a two-modifier gesture is taken before the text box
        // sees it, so binding an action to one the platform uses for editing (Ctrl+Shift+Z is Redo) takes that
        // editing gesture away in every text field of the main window. The session switch is a deliberate case of
        // this — Ctrl+Shift+arrow shadows select-by-word — which is why StaysActiveInTerminal's summary spells
        // out that the flag only settles the question for its one-modifier members.
        Assert.True(Live(SessionSwitch, ShortcutFocus.TextBox));
    }

    private static bool Live(ShortcutBinding binding, ShortcutFocus focus) =>
        ShortcutDispatchGate.IsBindingLive(binding, KeyGesture.Parse(binding.Gesture), focus);
}
