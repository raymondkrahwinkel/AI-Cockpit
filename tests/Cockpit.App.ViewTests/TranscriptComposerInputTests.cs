using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-1165: the composer's key handling, paste (text and image) and mention insertion, once — shared by
/// SessionView and AssistantChatView through <see cref="TranscriptComposerInput"/>. These exercise the shared
/// implementation directly, with a bare <see cref="TextBox"/> and fakes standing in for whichever view model
/// would normally answer each callback — the per-view differences (DataContext type, the busy/stop path) stay
/// covered by SessionMentionPickerViewTests and AssistantChatMentionPickerViewTests/AssistantChatComposerTests.
/// </summary>
[Collection("avalonia")]
public sealed class TranscriptComposerInputTests
{
    // 1x1 transparent PNG — just enough for Bitmap to decode (same fixture as PendingAttachmentDisposeTests).
    private static readonly byte[] TinyPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    private sealed class FakeCommand : ICommand
    {
        internal int ExecuteCount { get; private set; }
        internal bool CanExecuteValue { get; set; } = true;

#pragma warning disable CS0067 // never raised — nothing here re-queries CanExecute
        public event EventHandler? CanExecuteChanged;
#pragma warning restore CS0067

        public bool CanExecute(object? parameter) => CanExecuteValue;

        public void Execute(object? parameter) => ExecuteCount++;
    }

    private sealed class Harness
    {
        internal TextBox Box { get; } = new();
        internal MentionPickerViewModel? Picker { get; set; }
        internal bool Recall { get; set; }
        internal ICommand? StopIfBusy { get; set; }
        internal FakeCommand Send { get; } = new();
        internal byte[]? PastedImage { get; set; }
        internal bool ImageSinkAvailable { get; set; } = true;
        internal Bitmap? ClipboardBitmap { get; set; }
        internal string? ClipboardText { get; set; }
        internal TranscriptComposerInput Input { get; }

        internal Harness()
        {
            Input = new TranscriptComposerInput(
                Box,
                tryGetPastedBitmap: () => Task.FromResult(ClipboardBitmap),
                tryGetPastedText: () => Task.FromResult(ClipboardText),
                hasComposer: () => true,
                mentionPicker: () => Picker,
                recallLastQueuedMessage: () => Recall,
                resolveStopIfBusy: () => StopIfBusy,
                sendCommand: () => Send,
                resolvePastedImageSink: () => ImageSinkAvailable ? bytes => PastedImage = bytes : null);
        }

        internal void PressCtrlV()
        {
            var args = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.V, KeyModifiers = KeyModifiers.Control };
            Input.OnKeyDown(null, args);
        }

        internal KeyEventArgs PressKey(Key key, KeyModifiers modifiers = KeyModifiers.None)
        {
            var args = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers };
            Input.OnKeyDown(null, args);
            return args;
        }
    }

    [Fact]
    public void PastingText_InsertsAtTheCaret_ReplacingAnySelection() => HeadlessAvalonia.Run(() =>
    {
        var harness = new Harness { ClipboardText = "X" };
        harness.Box.Text = "ab";
        harness.Box.CaretIndex = 1;

        var down = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.V, KeyModifiers = KeyModifiers.Control };
        harness.Input.OnKeyDown(null, down);

        Assert.True(down.Handled);
        Assert.Equal("aXb", harness.Box.Text);
        Assert.Equal(2, harness.Box.CaretIndex);
    });

    [Fact]
    public void PastingAnImage_RoutesThePngBytesToTheResolvedSink_AndLeavesTextUntouched() => HeadlessAvalonia.Run(() =>
    {
        // Not disposed here: a real paste hands the bitmap over and _HandlePasteAsync owns disposing it.
        var harness = new Harness { ClipboardBitmap = new Bitmap(new MemoryStream(TinyPng)) };
        harness.Box.Text = "unchanged";

        harness.PressCtrlV();

        Assert.NotNull(harness.PastedImage);
        Assert.NotEmpty(harness.PastedImage!);
        Assert.Equal("unchanged", harness.Box.Text);
    });

    // Mirrors AssistantChatView pasting an image with no session yet to attach to (AC-953): the sink resolves
    // to null and the paste drops silently instead of throwing or falling through to a text insert.
    [Fact]
    public void PastingAnImage_WhenNoSinkIsAvailable_DropsSilently() => HeadlessAvalonia.Run(() =>
    {
        var harness = new Harness { ImageSinkAvailable = false, ClipboardBitmap = new Bitmap(new MemoryStream(TinyPng)) };
        harness.Box.Text = "unchanged";

        harness.PressCtrlV();

        Assert.Null(harness.PastedImage);
        Assert.Equal("unchanged", harness.Box.Text);
    });

    [Fact]
    public void AcceptingAMention_SplicesItInAtTheCaret_NotAtTheStartOrEndOfTheText() => HeadlessAvalonia.Run(() =>
    {
        var harness = new Harness();
        var picker = new MentionPickerViewModel(_ => Task.FromResult<IReadOnlyList<string>>(["src/Foo.cs"]), () => "/repo");
        harness.Box.Text = "hi @fo, thanks";
        picker.OnTextChanged(harness.Box.Text, caretIndex: 6); // caret right after "@fo"
        Assert.True(picker.IsOpen);
        Assert.NotNull(picker.Selected);
        harness.Picker = picker;
        harness.Box.CaretIndex = 6;

        var args = harness.PressKey(Key.Enter);

        Assert.True(args.Handled);
        Assert.False(picker.IsOpen);
        Assert.Equal("hi @src/Foo.cs , thanks", harness.Box.Text);
        Assert.Equal("hi @src/Foo.cs ".Length, harness.Box.CaretIndex);
        Assert.Equal(0, harness.Send.ExecuteCount);
    });

    [Fact]
    public void Enter_WithoutShift_SendsAndMarksTheEventHandled() => HeadlessAvalonia.Run(() =>
    {
        var harness = new Harness();
        harness.Box.Text = "hello";

        var args = harness.PressKey(Key.Enter);

        Assert.True(args.Handled);
        Assert.Equal(1, harness.Send.ExecuteCount);
    });

    [Fact]
    public void ShiftEnter_NeverSends_AndLeavesTheEventUnhandledForTheTextBoxsOwnNewline() => HeadlessAvalonia.Run(() =>
    {
        var harness = new Harness();
        harness.Box.Text = "hello";

        var args = harness.PressKey(Key.Enter, KeyModifiers.Shift);

        Assert.False(args.Handled);
        Assert.Equal(0, harness.Send.ExecuteCount);
    });
}
