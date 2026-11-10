namespace Cockpit.Plugin.Browser.Ipc;

internal sealed record BrowserTabCreate(Guid TabId, string Url, int Width = 800, int Height = 600, double Scale = 1);
internal sealed record BrowserTabSize(int Width, int Height, double Scale, long Revision);
internal sealed record BrowserTabInput(string Kind, int X = 0, int Y = 0, int Flags = 0, int Button = 0,
    int Count = 1, int DeltaX = 0, int DeltaY = 0, int Key = 0, int Character = 0, bool Up = false);
internal sealed record BrowserTabState(string Address, string Title, bool Loading, bool Back, bool Forward, string? Failure = null);
internal sealed record BrowserTabDevTools(string Method, IReadOnlyDictionary<string, object> Parameters);
internal sealed record BrowserTabFrame(BrowserIpcFrameDescriptor Buffer, long Revision, long FrameId,
    int X, int Y, int Width, int Height, double Scale);
