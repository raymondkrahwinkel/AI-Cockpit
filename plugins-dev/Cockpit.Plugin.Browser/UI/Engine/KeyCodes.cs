using Avalonia.Input;

namespace Cockpit.Plugin.Browser.UI.Engine;

// Avalonia's Key to the Windows virtual-key code CEF expects on every platform. Text arrives separately as Char
// events, so only keys that act (editing, navigation, shortcuts) need a code here.
// ponytail: no OEM punctuation keys; typed text covers them, add when a shortcut needs one.
internal static class KeyCodes
{
    // AC-1728: a key an agent names ("Enter", "ArrowDown", "Backspace"), as Avalonia's Key or a DOM key name.
    public static int FromName(string name)
    {
        var key = name switch
        {
            "ArrowUp" => nameof(Key.Up),
            "ArrowDown" => nameof(Key.Down),
            "ArrowLeft" => nameof(Key.Left),
            "ArrowRight" => nameof(Key.Right),
            "Backspace" => nameof(Key.Back),
            "Esc" => nameof(Key.Escape),
            " " => nameof(Key.Space),
            _ => name,
        };
        // Enum.TryParse also takes "42" as a Key; a number is not a key name.
        return int.TryParse(key, out _) == false && Enum.TryParse<Key>(key, ignoreCase: true, out var parsed) ? ToWindows(parsed) : 0;
    }

    public static int ToWindows(Key key) => key switch
    {
        >= Key.A and <= Key.Z => 0x41 + (key - Key.A),
        >= Key.D0 and <= Key.D9 => 0x30 + (key - Key.D0),
        >= Key.NumPad0 and <= Key.NumPad9 => 0x60 + (key - Key.NumPad0),
        >= Key.F1 and <= Key.F24 => 0x70 + (key - Key.F1),
        Key.Back => 0x08,
        Key.Tab => 0x09,
        Key.Enter => 0x0D,
        Key.LeftShift or Key.RightShift => 0x10,
        Key.LeftCtrl or Key.RightCtrl => 0x11,
        Key.LeftAlt or Key.RightAlt => 0x12,
        Key.Escape => 0x1B,
        Key.Space => 0x20,
        Key.PageUp => 0x21,
        Key.PageDown => 0x22,
        Key.End => 0x23,
        Key.Home => 0x24,
        Key.Left => 0x25,
        Key.Up => 0x26,
        Key.Right => 0x27,
        Key.Down => 0x28,
        Key.Insert => 0x2D,
        Key.Delete => 0x2E,
        _ => 0,
    };
}
