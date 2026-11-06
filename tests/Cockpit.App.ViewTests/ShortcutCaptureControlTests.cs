using Avalonia.Input;
using Cockpit.App.Controls;

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
        // A modifier held while pressing the key is dropped: the push-to-talk binding is a single held key.
        var stored = ShortcutCaptureControl.FormatCapturedKey(Key.F9, KeyModifiers.Control, ShortcutCaptureMode.SingleKey);

        Assert.Equal("F9", stored);
        Assert.True(Enum.TryParse<Key>(stored, ignoreCase: true, out var parsed));
        Assert.Equal(Key.F9, parsed);
    }
}
