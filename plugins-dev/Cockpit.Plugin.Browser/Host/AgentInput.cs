using Xilium.CefGlue;

namespace Cockpit.Plugin.Browser.Host;

// AC-1728: an agent's input, sent the way the operator's own mouse and keys are, through CEF's offscreen host. Call
// only on CEF's UI thread, after ControlPolicy allowed it. Coordinates are the page's CSS pixels at 100% zoom.
internal static class AgentInput
{
    public static void Click(CefBrowserHost host, int x, int y)
    {
        var mouse = new CefMouseEvent(x, y, CefEventFlags.None);
        host.SetFocus(true);
        host.SendMouseMoveEvent(mouse, false);
        host.SendMouseClickEvent(mouse, CefMouseButtonType.Left, false, 1);
        host.SendMouseClickEvent(mouse, CefMouseButtonType.Left, true, 1);
    }

    public static void Type(CefBrowserHost host, string text)
    {
        host.SetFocus(true);
        foreach (var character in text)
        {
            host.SendKeyEvent(new CefKeyEvent { EventType = CefKeyEventType.Char, Character = character, UnmodifiedCharacter = character, WindowsKeyCode = character });
        }
    }

    // A Windows key code from KeyCodes.FromName; Enter also sends its character, which is what submits a form.
    public static void Key(CefBrowserHost host, int code)
    {
        host.SetFocus(true);
        host.SendKeyEvent(new CefKeyEvent { EventType = CefKeyEventType.RawKeyDown, WindowsKeyCode = code });
        if (code == 0x0D)
        {
            host.SendKeyEvent(new CefKeyEvent { EventType = CefKeyEventType.Char, Character = '\r', UnmodifiedCharacter = '\r', WindowsKeyCode = code });
        }

        host.SendKeyEvent(new CefKeyEvent { EventType = CefKeyEventType.KeyUp, WindowsKeyCode = code });
    }

    public static void Scroll(CefBrowserHost host, int x, int y, int dx, int dy) => host.SendMouseWheelEvent(new CefMouseEvent(x, y, CefEventFlags.None), dx, dy);
}
