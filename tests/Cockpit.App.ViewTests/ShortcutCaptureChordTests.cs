using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Cockpit.App.Controls;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-608: recording a shortcut takes a lone key but reportedly not a chord. The sibling suite
/// (<see cref="ShortcutCaptureControlTests"/>) only exercises <c>FormatCapturedKey</c> — the pure function — so
/// nothing here ever drove a real key press through the control. These do, so "the control itself is fine" is a
/// measurement rather than a reading of the source.
/// </summary>
[Collection("avalonia")]
public class ShortcutCaptureChordTests
{
    [Theory]
    [InlineData(Key.M, KeyModifiers.None, "M")]
    [InlineData(Key.M, KeyModifiers.Control, "Ctrl+M")]
    [InlineData(Key.M, KeyModifiers.Control | KeyModifiers.Shift, "Ctrl+Shift+M")]
    [InlineData(Key.P, KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift, "Ctrl+Shift+Alt+P")]
    public void RecordingAKeyPress_StoresIt_ChordOrNot(Key key, KeyModifiers modifiers, string expected) =>
        HeadlessAvalonia.Run(() =>
        {
            var (control, record) = _Recording();

            _Press(record, key, modifiers);

            Assert.Equal(expected, control.Gesture);
        });

    [Fact]
    public void TheModifiersAreNotMistakenForTheChordItself() =>
        HeadlessAvalonia.Run(() =>
        {
            // Holding Ctrl+Shift raises a key press per modifier before the real key arrives. Each of those has to
            // leave the field recording, or the chord is decided by whichever modifier was pressed first.
            var (control, record) = _Recording();

            _Press(record, Key.LeftCtrl, KeyModifiers.Control);
            _Press(record, Key.LeftShift, KeyModifiers.Control | KeyModifiers.Shift);

            Assert.True(string.IsNullOrEmpty(control.Gesture));

            _Press(record, Key.M, KeyModifiers.Control | KeyModifiers.Shift);

            Assert.Equal("Ctrl+Shift+M", control.Gesture);
        });

    [Fact]
    public void EscapeCancels_AndLeavesTheOldBindingStanding() =>
        HeadlessAvalonia.Run(() =>
        {
            var (control, record) = _Recording("Ctrl+N");

            _Press(record, Key.Escape, KeyModifiers.None);

            Assert.Equal("Ctrl+N", control.Gesture);
        });

    /// <summary>A capture field in a shown window, already recording — the state a click on it produces.</summary>
    private static (ShortcutCaptureControl Control, Button Record) _Recording(string gesture = "")
    {
        var control = new ShortcutCaptureControl { Gesture = gesture, Mode = ShortcutCaptureMode.Chord };
        var window = new Window { Content = control, Width = 400, Height = 200 };
        window.Show();

        var record = control.GetVisualDescendants().OfType<Button>().First();
        record.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        return (control, record);
    }

    private static void _Press(Button record, Key key, KeyModifiers modifiers) =>
        record.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = key,
            KeyModifiers = modifiers,
        });
}
