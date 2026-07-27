using Avalonia;
using Avalonia.Headless;
using Avalonia.Input;
using FluentAssertions;
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
    [Fact]
    public void TypingAWordMadeOfShortcuts_TriggersNoneOfThem() => _OnTheSurface(surface =>
    {
        var selection = _Model(surface);
        _OpenNote(surface, new Point(600, 400));

        // Read after the note is open, since opening one needs a region and taking one is what A does.
        var region = selection.Selection;
        surface.KeyTextInput("Window ABDOHPR");

        selection.PickingWindow.Should().BeFalse("W was a letter, not the window tool");
        selection.Drawing.Should().BeFalse();
        selection.Outlining.Should().BeFalse();
        selection.Redacting.Should().BeFalse();
        selection.Highlighting.Should().BeFalse();
        selection.Selection.Should().Be(region, "and A did not take the whole capture");
        selection.IsClosed.Should().BeFalse("nor did anything confirm or cancel it");
        selection.Typed.Should().Be("Window ABDOHPR", "all of it went into the note");
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

        selection.Typing.Should().BeFalse("the note is closed");
        selection.IsClosed.Should().BeFalse("but the surface is not");
        selection.Marks.Should().ContainSingle().Which.Should().BeOfType<TextMark>().Which
            .Text.Should().Be("this one", "what was typed is kept rather than thrown away by the key that ends it");

        surface.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);

        selection.IsClosed.Should().BeTrue("the second press is the one that cancels");
        selection.Result.Should().BeNull();
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

        selection.IsClosed.Should().BeFalse();
        selection.Marks.Should().ContainSingle();

        surface.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);

        selection.Result.Should().NotBeNull("with no note open, Enter is the shortcut again");
    });

    /// <summary>A note with nothing typed into it is an invisible mark, and an operator who opened one by accident should not have to find it again to take it off.</summary>
    [Fact]
    public void ANoteWithNothingTypedIntoIt_LeavesNothingBehind() => _OnTheSurface(surface =>
    {
        var selection = _Model(surface);
        _OpenNote(surface, new Point(600, 400));

        surface.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);

        selection.Marks.Should().BeEmpty();
    });

    /// <summary>Backspace takes back a character rather than reaching the surface, where it means nothing and would be silently swallowed.</summary>
    [Fact]
    public void BackspaceTakesBackACharacter() => _OnTheSurface(surface =>
    {
        var selection = _Model(surface);
        _OpenNote(surface, new Point(600, 400));
        surface.KeyTextInput("this onex");

        surface.KeyPressQwerty(PhysicalKey.Backspace, RawInputModifiers.None);

        selection.Typed.Should().Be("this one");
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

        selection.Outlining.Should().BeTrue();
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

        selection.Marks.Should().ContainSingle().Which.Should().BeOfType<TextMark>().Which.Text.Should().Be("first");
        selection.Typing.Should().BeTrue("and the next one is open where the click landed");
    });

    private static void _OpenNote(ScreenshotSelectionWindow surface, Point at)
    {
        surface.KeyPressQwerty(PhysicalKey.A, RawInputModifiers.None);
        surface.KeyPressQwerty(PhysicalKey.T, RawInputModifiers.None);
        surface.MouseDown(at, MouseButton.Left);
        surface.MouseUp(at, MouseButton.Left);

        _Model(surface).Typing.Should().BeTrue("the click opened a note, which the rest of the test is about");
    }

    private static void _OnTheSurface(Action<ScreenshotSelectionWindow> assert) => HeadlessAvalonia.Run(() =>
    {
        var surface = Screenshotter.BuildScene(ScreenshotSelectionScene.Idle, SurfaceWidth, SurfaceHeight)
            .Should().BeOfType<ScreenshotSelectionWindow>().Subject;

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
