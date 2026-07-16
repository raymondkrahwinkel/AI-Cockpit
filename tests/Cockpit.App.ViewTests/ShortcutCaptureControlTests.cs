using Avalonia.Input;
using Cockpit.App.Controls;
using FluentAssertions;

namespace Cockpit.App.ViewTests;

/// <summary>
/// The push-to-talk key field reuses <see cref="ShortcutCaptureControl"/> in
/// <see cref="ShortcutCaptureMode.SingleKey"/> mode. What matters is the form it stores: the push-to-talk key is
/// read back with <c>Enum.TryParse&lt;Key&gt;</c> (<c>PushToTalkKeyGate</c>), so single-key mode must store the
/// bare key name and not an Avalonia gesture string — a chord like "Ctrl+F9" would not round-trip through that.
/// </summary>
public class ShortcutCaptureControlTests
{
    [Fact]
    public void SingleKeyMode_StoresTheBareKeyName_SoPushToTalkKeyGateCanParseItBack()
    {
        var stored = ShortcutCaptureControl.FormatCapturedKey(Key.F9, KeyModifiers.None, ShortcutCaptureMode.SingleKey);

        stored.Should().Be("F9");
        Enum.TryParse<Key>(stored, ignoreCase: true, out var parsed).Should().BeTrue();
        parsed.Should().Be(Key.F9);
    }

    [Fact]
    public void SingleKeyMode_IgnoresModifiers_BecauseAHeldKeyIsNotAChord()
    {
        // Even if a modifier is down when the key is pressed, the push-to-talk binding is a single held key.
        var stored = ShortcutCaptureControl.FormatCapturedKey(Key.F9, KeyModifiers.Control, ShortcutCaptureMode.SingleKey);

        stored.Should().Be("F9");
    }

    [Fact]
    public void ChordMode_StoresTheFullGesture_WithItsModifiers()
    {
        var stored = ShortcutCaptureControl.FormatCapturedKey(Key.P, KeyModifiers.Control | KeyModifiers.Shift, ShortcutCaptureMode.Chord);

        stored.Should().Be("Ctrl+Shift+P");
    }
}
