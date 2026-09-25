using Material.Icons;
using Cockpit.Plugin.Diagram.Whiteboard;
using Cockpit.Plugin.Diagram.Wireframe;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.Plugin.Diagram;

// F2.13 (AC-1401): the UI half — everything that needs a window. DiagramPlugin (the backend project) owns the
// registries, their channel and the MCP tools; this hands the windows that channel, their only way in (AC-1400).
public sealed class DiagramUi : ICockpitPluginUi, IDisposable
{
    // Reused as the diagrams dialog's ShowDialogAsync singleInstanceKey — one list at a time (AC-850).
    private const string ListDialogKey = "diagram.list";

    // W-2/AC-843: the whiteboards dialog's own singleInstanceKey, same precedent, one folder over.
    private const string WhiteboardListDialogKey = "whiteboard.list";

    // AC-873: the wireframes dialog's own singleInstanceKey, same precedent, another folder over.
    private const string WireframeListDialogKey = "wireframe.list";

    private SurfaceWindowOpener? _opener;

    public void InitializeUi(ICockpitUiHost host)
    {
        // AC-850: the Diagram/Whiteboard tabs and the diagrams-list tab are gone — every ⋯ item now opens a
        // window or a dialog instead of a workspace. AC-896: the "New ..." actions moved into their panel's
        // own header, next to Refresh — only the panel openers stay here.
        host.AddToolbarAction(new ToolbarAction("Diagrams", MaterialIconKind.FormatListBulleted,
            () => host.ShowDialogAsync("Diagrams", () => new DiagramListDialogBody(host, host.Channel), ListDialogKey, width: 520, height: 600)));

        host.AddToolbarAction(new ToolbarAction("Whiteboards", MaterialIconKind.FormatListBulleted,
            () => host.ShowDialogAsync("Whiteboards", () => new WhiteboardListDialogBody(host, host.Channel), WhiteboardListDialogKey, width: 520, height: 600)));

        host.AddToolbarAction(new ToolbarAction("Wireframes", MaterialIconKind.FormatListBulleted,
            () => host.ShowDialogAsync("Wireframes", () => new WireframeListDialogBody(host, host.Channel), WireframeListDialogKey, width: 520, height: 600)));

        var settings = new DiagramSettings(host.Storage);
        host.AddSettings(() => new DiagramSettingsControl(host, settings));

        _opener = new SurfaceWindowOpener(host, host.Channel);
    }

    public void Dispose() => _opener?.Dispose();
}
