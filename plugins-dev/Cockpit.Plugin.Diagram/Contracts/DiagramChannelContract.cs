using System.Text.Json;

namespace Cockpit.Plugin.Diagram;

// The wire protocol between DiagramChannel (backend) and the UI project's channel clients (F2.13/AC-1401): action
// names, the JSON options and the small argument helpers, linked as source into both assemblies so the plugin
// folder still carries one copy — same shape as GitStatus's Contracts/GitStatusChannel.cs (AC-1390).
internal static class DiagramChannelContract
{
    public const string DiagramPrefix = "diagram.";
    public const string WhiteboardPrefix = "whiteboard.";
    public const string WireframePrefix = "wireframe.";

    // [surfaceId, kind, title, source, callerPaneId]: an agent's open_* tool asks the UI part for a window.
    public const string OpenSurface = "surface.open";

    // [paneId]: a session ended, so a window bound to it can say so (SurfaceSessionBinding).
    public const string SessionClosed = "session.closed";

    // [paneId] -> SessionBindResult: whether that pane is still open right now, and its operator-visible name.
    public const string SessionBind = "session.bind";

    // No args -> OpenCockpitSession[]: the open-sessions picker SurfaceSessionBinding.ShowSessionPicker offers.
    public const string SessionList = "session.list";

    // The UI part attaches once it listens for OpenSurface and detaches when it stops; an open_* tool asks no one else.
    public const string AttachUi = "ui.attach";
    public const string DetachUi = "ui.detach";

    // "<prefix>Served" answers only when this backend has that registry, so a window can tell "no registry here".
    public const string Served = "Served";

    // AC-915: the three sheet sizes the wireframe format has words for — WireframeRenderer.SizeOf (Avalonia's Size)
    // and WireframeMcpTools._ViewportInfo (the cockpit-wireframe MCP answer) both read these, so a design change to
    // one lands in both instead of the two drifting apart (F2.13/AC-1401).
    public const double DesktopViewportWidth = 960;
    public const double DesktopViewportHeight = 640;
    public const double TabletViewportWidth = 768;
    public const double TabletViewportHeight = 1024;
    public const double MobileViewportWidth = 390;
    public const double MobileViewportHeight = 844;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string ReadString(JsonElement args, int index) =>
        args[index].GetString() ?? throw new JsonException($"Channel argument {index} is null where a string is required.");

    public static T ReadArg<T>(JsonElement args, int index) =>
        args[index].Deserialize<T>(Json) ?? throw new JsonException($"Channel argument {index} is null where a {typeof(T).Name} is required.");
}
