using Avalonia;
using Avalonia.Headless;
using Avalonia.Input;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Abstractions.Screenshots;

namespace Cockpit.App.ViewTests;

/// <summary>
/// Typing a note onto the capture (AC-363), driven through the surface's own keys — which is the whole of what is
/// dangerous about this mark and cannot be seen from the view model.
/// </summary>
/// <remarks>
/// The surface owns every key. While a note is open each of them is a letter instead, and getting that wrong does
/// not produce a wrong-looking label: it picks a window, blanks the region and takes the shot, on an operator who
/// was typing a word.
/// </remarks>
[Collection("avalonia")]
public class ScreenshotLabelTests
{
    private const int SurfaceWidth = 1440;
    private const int SurfaceHeight = 900;

    /// <summary>
    /// The acceptance criterion, said as the sentence that breaks it: typing "Window" must not pick a window. Every
    /// letter in it is a shortcut — W picks a window, D draws, O frames, and the rest are equally live.
    /// </summary>
    /// <remarks>
    /// The keys are pressed as well as the text being sent. A keyboard produces both, and it is the key half that
    /// the shortcuts listen to — a test that only sent the text would exercise none of the standing-down this is
    /// about, and would pass on a surface that had none.
    /// </remarks>
    [Fact]
    public void TypingAWordMadeOfShortcuts_TriggersNoneOfThem() => _OnTheSurface(surface =>
    {
        var selection = _Model(surface);
        _OpenNote(surface, new Point(600, 400));

        // Read after the note is open, since opening one needs a region and taking one is what A does.
        var region = selection.Selection;
        // Each of them once. Pressing one twice would toggle its tool on and straight back off, and the assertion
        // that it never came on would then pass on a surface where every key was live.
        foreach (var key in new[]
                 {
                     PhysicalKey.W, PhysicalKey.A, PhysicalKey.B, PhysicalKey.D,
                     PhysicalKey.H, PhysicalKey.O, PhysicalKey.P, PhysicalKey.R,
                 })
        {
            surface.KeyPressQwerty(key, RawInputModifiers.None);
        }

        surface.KeyTextInput("Window ABDOHPR");

        Assert.False(selection.PickingWindow, "W was a letter, not the window tool");
        Assert.False(selection.Drawing);
        Assert.False(selection.Outlining);
        Assert.False(selection.Redacting);
        Assert.False(selection.Highlighting);
        Assert.Equal(region, selection.Selection);
        Assert.False(selection.IsClosed, "nor did anything confirm or cancel it");
        Assert.Equal("Window ABDOHPR", selection.Typed);
    });

    /// <summary>
    /// Escape ends the note, and only a further Escape cancels the capture. One press for the operator who wants
    /// their note; two for the one who wants out.
    /// </summary>
    [Fact]
    public void EscapeEndsTheNote_AndOnlyAFurtherEscapeCancelsTheCapture() => _OnTheSurface(surface =>
    {
        var selection = _Model(surface);
        _OpenNote(surface, new Point(600, 400));
        surface.KeyTextInput("this one");

        surface.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);

        Assert.False(selection.Typing, "the note is closed");
        Assert.False(selection.IsClosed, "but the surface is not");
        Assert.Equal(
            "this one",
            Assert.IsType<TextMark>(Assert.Single(selection.Marks)).Text);

        surface.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);

        Assert.True(selection.IsClosed, "the second press is the one that cancels");
        Assert.Null(selection.Result);
    });

    /// <summary>
    /// Enter finishes the note rather than taking the shot. Confirming there would throw away the label by the
    /// very key an operator presses to say they have finished typing it.
    /// </summary>
    [Fact]
    public void EnterFinishesTheNote_RatherThanTakingTheShot() => _OnTheSurface(surface =>
    {
        var selection = _Model(surface);
        _OpenNote(surface, new Point(600, 400));
        surface.KeyTextInput("expected 12 here");

        surface.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);

        Assert.False(selection.IsClosed);
        Assert.Single(selection.Marks);

        surface.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);

        Assert.NotNull(selection.Result);
    });

    /// <summary>A note with nothing typed into it is an invisible mark, and an operator who opened one by accident should not have to find it again to take it off.</summary>
    [Fact]
    public void ANoteWithNothingTypedIntoIt_LeavesNothingBehind() => _OnTheSurface(surface =>
    {
        var selection = _Model(surface);
        _OpenNote(surface, new Point(600, 400));

        surface.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);

        Assert.Empty(selection.Marks);
    });

    /// <summary>Backspace takes back a character rather than reaching the surface, where it means nothing and would be silently swallowed.</summary>
    [Fact]
    public void BackspaceTakesBackACharacter() => _OnTheSurface(surface =>
    {
        var selection = _Model(surface);
        _OpenNote(surface, new Point(600, 400));
        surface.KeyTextInput("this onex");

        surface.KeyPressQwerty(PhysicalKey.Backspace, RawInputModifiers.None);

        Assert.Equal("this one", selection.Typed);
    });

    /// <summary>
    /// The shortcuts come back the moment the note is closed. A tool that stood the keys down and left them down
    /// would be worse than one that never stood them down at all.
    /// </summary>
    [Fact]
    public void OnceTheNoteIsClosed_TheKeysAreShortcutsAgain() => _OnTheSurface(surface =>
    {
        var selection = _Model(surface);
        _OpenNote(surface, new Point(600, 400));
        surface.KeyTextInput("done");
        surface.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);

        surface.KeyPressQwerty(PhysicalKey.O, RawInputModifiers.None);

        Assert.True(selection.Outlining);
    });

    /// <summary>Clicking somewhere else puts down the note you had rather than throwing it away, and opens another where you clicked.</summary>
    [Fact]
    public void ClickingElsewhere_KeepsTheNoteYouHad_AndOpensAnother() => _OnTheSurface(surface =>
    {
        var selection = _Model(surface);
        _OpenNote(surface, new Point(600, 400));
        surface.KeyTextInput("first");

        surface.MouseDown(new Point(800, 500), MouseButton.Left);
        surface.MouseUp(new Point(800, 500), MouseButton.Left);

        Assert.Equal("first", Assert.IsType<TextMark>(Assert.Single(selection.Marks)).Text);
        Assert.True(selection.Typing, "and the next one is open where the click landed");
    });

    /// <summary>
    /// Clicking the same spot twice opens another note rather than taking the shot. A double-click inside what is
    /// marked out is how the surface is confirmed — which makes the ordinary act of clicking a place twice, with a
    /// tool in hand, a way to hand over a screenshot you were still working on.
    /// </summary>
    [Fact]
    public void ClickingTheSameSpotTwice_OpensAnotherNote_RatherThanTakingTheShot() => _OnTheSurface(surface =>
    {
        var selection = _Model(surface);
        var spot = new Point(600, 400);
        _OpenNote(surface, spot);
        surface.KeyTextInput("first");

        surface.MouseDown(spot, MouseButton.Left);
        surface.MouseUp(spot, MouseButton.Left);

        Assert.False(selection.IsClosed, "the second click was a note, not a confirmation");
        Assert.Null(selection.Result);
        Assert.True(selection.Typing, "and it opened another note where it landed");
    });

    /// <summary>
    /// Drags a region out rather than pressing A for the whole capture, so that a stray A during typing would
    /// visibly change what is marked out. Starting from everything would make that assertion say nothing.
    /// </summary>
    /// <summary>
    /// The surface can hold focus, and does. Keys reach a window whether or not anything is focused; text goes to
    /// the focused element, and every control on this surface is deliberately unfocusable so that clicking a tool
    /// never costs you the keyboard — which leaves nothing to type into unless the window itself takes it.
    /// </summary>
    /// <remarks>
    /// A thin assertion for a defect that was anything but. Without it a note opened, showed its plate and
    /// swallowed every character, and every test in this file passed throughout: the headless harness delivers
    /// text input to the window regardless of what is focused, so it was answering a question the real keyboard
    /// asks differently. Raymond found it in the first minute of using it.
    /// </remarks>
    [Fact]
    public void TheSurfaceTakesFocus_SoThatTypingHasSomewhereToLand() => _OnTheSurface(surface =>
    {
        Assert.True(surface.Focusable, "text input goes to the focused element, and nothing else here can be one");
        Assert.True(surface.IsFocused, "and the surface asks for it as it opens");
    });

    private static void _OpenNote(ScreenshotSelectionWindow surface, Point at)
    {
        surface.MouseDown(new Point(200, 200), MouseButton.Left);
        surface.MouseMove(new Point(1100, 750), RawInputModifiers.LeftMouseButton);
        surface.MouseUp(new Point(1100, 750), MouseButton.Left);

        surface.KeyPressQwerty(PhysicalKey.T, RawInputModifiers.None);
        surface.MouseDown(at, MouseButton.Left);
        surface.MouseUp(at, MouseButton.Left);

        Assert.True(_Model(surface).Typing, "the click opened a note, which the rest of the test is about");
    }

    private static void _OnTheSurface(Action<ScreenshotSelectionWindow> assert) => HeadlessAvalonia.Run(() =>
    {
        var surface = Assert.IsType<ScreenshotSelectionWindow>(Screenshotter.BuildScene(ScreenshotSelectionScene.Idle, SurfaceWidth, SurfaceHeight));

        surface.Show();
        try
        {
            assert(surface);
        }
        finally
        {
            // Some of these end with the surface already closed — cancelling is half of what they are about.
            if (surface.IsVisible)
            {
                surface.Close();
            }
        }
    });

    private static ScreenshotSelectionViewModel _Model(ScreenshotSelectionWindow surface) =>
        surface.DataContext as ScreenshotSelectionViewModel
        ?? throw new InvalidOperationException("The surface was built without its view model.");
}
