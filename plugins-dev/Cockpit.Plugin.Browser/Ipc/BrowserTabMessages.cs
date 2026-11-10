namespace Cockpit.Plugin.Browser.Ipc;

internal sealed record BrowserTabCreate(Guid TabId, string Url, int Width = 800, int Height = 600, double Scale = 1);
internal sealed record BrowserTabSize(int Width, int Height, double Scale, long Revision);
internal sealed record BrowserTabInput(string Kind, int X = 0, int Y = 0, int Flags = 0, int Button = 0,
    int Count = 1, int DeltaX = 0, int DeltaY = 0, int Key = 0, int Character = 0, bool Up = false);
internal sealed record BrowserTabState(string Address, string Title, bool Loading, bool Back, bool Forward, string? Failure = null, BrowserTabHeld? Held = null, BrowserTabImeRange? Ime = null);
internal sealed record BrowserTabIme(string Kind, long Id, string Text = "", int Cursor = 0);
internal sealed record BrowserTabImeRect(double X, double Y, double Width, double Height);
internal sealed record BrowserTabImeRange(long Id, long SizeRevision, int From, int To, int Cursor, double Scale, BrowserTabImeRect[] Bounds);
internal sealed record BrowserTabControl(string? Origin, Guid? Grant, long Revision);
internal sealed record BrowserTabAgent(string Kind, Guid Grant, bool NeedsFocus, int X = 0, int Y = 0, int Key = 0, string? Text = null, string? Frames = null);
internal sealed record BrowserTabHeld(long Id, string Method, string Url);
internal sealed record BrowserTabRelease(long Id, bool Send);
internal sealed record BrowserTabFileDialog(int Mode, string Title, string DefaultPath, string[] Filters);
internal sealed record BrowserTabDevTools(string Method, IReadOnlyDictionary<string, object> Parameters);
internal sealed record BrowserTabFrame(BrowserIpcFrameDescriptor Buffer, long Revision, long FrameId,
    int X, int Y, int Width, int Height, double Scale);
