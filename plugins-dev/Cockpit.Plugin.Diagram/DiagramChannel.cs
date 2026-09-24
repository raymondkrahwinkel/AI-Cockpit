using System.Text.Json;
using Cockpit.Core.Abstractions.Diagrams;
using Cockpit.Core.Abstractions.Whiteboard;
using Cockpit.Core.Abstractions.Wireframe;
using Cockpit.Plugins.Abstractions;
using D = Cockpit.Core.Abstractions.Diagrams.IDiagramAccessRegistry;
using W = Cockpit.Core.Abstractions.Whiteboard.IWhiteboardAccessRegistry;
using F = Cockpit.Core.Abstractions.Wireframe.IWireframeAccessRegistry;

namespace Cockpit.Plugin.Diagram;

// AC-1400 (F2.12): the backend half of Diagram's channel — the registries' members as actions and events, named after
// the member, with its arguments in order as one JSON array (an event's first element is the surface id). Transport
// only: the registry stays the one document model and nothing here keeps state of its own.
internal sealed class DiagramChannel : IDisposable
{
    public const string DiagramPrefix = "diagram.";
    public const string WhiteboardPrefix = "whiteboard.";
    public const string WireframePrefix = "wireframe.";

    // [surfaceId, kind, title, source, callerPaneId]: an agent's open_* tool asks the UI part for a window.
    public const string OpenSurface = "surface.open";

    // [paneId]: a session ended, so a window bound to it can say so (SurfaceSessionBinding).
    public const string SessionClosed = "session.closed";

    // Invoked by the UI part once it listens for OpenSurface; before that, an open_* tool has nobody to ask.
    public const string AttachUi = "ui.attach";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ICockpitHost _host;
    private readonly List<IDisposable> _handles = [];
    private readonly List<Action> _detach = [];
    private volatile bool _uiAttached;

    public DiagramChannel(ICockpitHost host, D? diagrams, W? whiteboards, F? wireframes)
    {
        _host = host;
        _Do(AttachUi, _ => _uiAttached = true);

        EventHandler<string> closed = (_, paneId) => _Publish(SessionClosed, paneId);
        host.Sessions.SessionClosed += closed;
        _detach.Add(() => host.Sessions.SessionClosed -= closed);

        if (diagrams is not null)
        {
            _Diagrams(diagrams);
        }

        if (whiteboards is not null)
        {
            _Whiteboards(whiteboards);
        }

        if (wireframes is not null)
        {
            _Wireframes(wireframes);
        }
    }

    // Asks the UI part to open a window on a surface an agent created. False when there is no UI part to ask (a
    // backend on its own), so the tool can say nothing opened rather than wait for a window that never comes.
    public bool RequestOpen(string kind, string surfaceId, string title, string? source, string callerPaneId)
    {
        if (!_uiAttached)
        {
            return false;
        }

        _Publish(OpenSurface, surfaceId, kind, title, source, callerPaneId);
        return true;
    }

    public void Dispose()
    {
        foreach (var handle in _handles)
        {
            handle.Dispose();
        }

        foreach (var detach in _detach)
        {
            detach();
        }
    }

    private void _Diagrams(D registry)
    {
        const string p = DiagramPrefix;
        _Do(p + nameof(D.SurfaceOpened), a => registry.SurfaceOpened(ReadString(a, 0), ReadString(a, 1), ReadString(a, 2)));
        _Do(p + nameof(D.SurfaceClosed), a => registry.SurfaceClosed(ReadString(a, 0)));
        _Do(p + nameof(D.UpdateText), a => registry.UpdateText(ReadString(a, 0), ReadString(a, 1)));
        _Do(p + nameof(D.Disconnect), a => registry.Disconnect(ReadString(a, 0)));
        _On(p + nameof(D.ListSurfaces), a => registry.ListSurfaces(ReadString(a, 0)));
        _On(p + nameof(D.IsCoupledByAnother), a => registry.IsCoupledByAnother(ReadString(a, 0), ReadString(a, 1)));
        _On(p + nameof(D.Couple), a => _Refusal(() => registry.Couple(ReadString(a, 0), ReadString(a, 1))));
        _On(p + nameof(D.ApplyHandEdit), a => registry.ApplyHandEdit(ReadString(a, 0), ReadArg<DiagramHandEdit>(a, 1)));
        _On(p + nameof(D.EditSupport), a => registry.EditSupport(ReadString(a, 0)));
        _On(p + nameof(D.EntityAttributes), a => registry.EntityAttributes(ReadString(a, 0), ReadString(a, 1)));
        _On(p + nameof(D.History), a => registry.History(ReadString(a, 0)));
        _On(p + nameof(D.Revert), a => registry.Revert(ReadString(a, 0), ReadString(a, 1)));
        _Do(p + nameof(D.HoldObject), a => registry.HoldObject(ReadString(a, 0), ReadString(a, 1)));
        _Do(p + nameof(D.ReleaseObject), a => registry.ReleaseObject(ReadString(a, 0), ReadString(a, 1)));
        _On(p + nameof(D.ResolveProposal), a => registry.ResolveProposal(ReadString(a, 0), ReadArg<HashSet<int>>(a, 1)));
        _On(p + nameof(D.DiscardProposal), a => registry.DiscardProposal(ReadString(a, 0)));

        Action<string, string> text = (surfaceId, value) => _Publish(p + nameof(D.TextChanged), surfaceId, value);
        Action<DiagramCouplingChange> coupling = change => _Publish(p + nameof(D.CouplingChanged), change.SurfaceId, change);
        Action<string, DiagramProposal?> proposal = (surfaceId, value) => _Publish(p + nameof(D.ProposalChanged), surfaceId, value);
        Action<string> history = surfaceId => _Publish(p + nameof(D.HistoryChanged), surfaceId);
        registry.TextChanged += text;
        registry.CouplingChanged += coupling;
        registry.ProposalChanged += proposal;
        registry.HistoryChanged += history;
        _detach.Add(() =>
        {
            registry.TextChanged -= text;
            registry.CouplingChanged -= coupling;
            registry.ProposalChanged -= proposal;
            registry.HistoryChanged -= history;
        });
    }

    private void _Whiteboards(W registry)
    {
        const string p = WhiteboardPrefix;
        _Do(p + nameof(W.SurfaceOpened), a => registry.SurfaceOpened(ReadString(a, 0), ReadString(a, 1), ReadArg<byte[]>(a, 2)));
        _Do(p + nameof(W.SurfaceClosed), a => registry.SurfaceClosed(ReadString(a, 0)));
        _Do(p + nameof(W.UpdateSnapshot), a => registry.UpdateSnapshot(ReadString(a, 0), ReadArg<byte[]>(a, 1)));
        _Do(p + nameof(W.Disconnect), a => registry.Disconnect(ReadString(a, 0)));
        _On(p + nameof(W.Couple), a => _Refusal(() => registry.Couple(ReadString(a, 0), ReadString(a, 1))));
        _On(p + nameof(W.Grant), a => _Refusal(() => registry.Grant(ReadString(a, 0), ReadString(a, 1), ReadArg<WhiteboardCapability>(a, 2))));
        _On(p + nameof(W.History), a => registry.History(ReadString(a, 0)));
        _On(p + nameof(W.Revert), a => registry.Revert(ReadString(a, 0), ReadString(a, 1)));

        Action<WhiteboardCouplingChange> coupling = change => _Publish(p + nameof(W.CouplingChanged), change.SurfaceId, change);
        Action<string, string, WhiteboardPlacement> placed = (surfaceId, objectId, placement) => _Publish(p + nameof(W.ObjectPlaced), surfaceId, objectId, placement);
        Action<string, string> erased = (surfaceId, objectId) => _Publish(p + nameof(W.ObjectErased), surfaceId, objectId);
        Action<string> history = surfaceId => _Publish(p + nameof(W.HistoryChanged), surfaceId);
        registry.CouplingChanged += coupling;
        registry.ObjectPlaced += placed;
        registry.ObjectErased += erased;
        registry.HistoryChanged += history;
        _detach.Add(() =>
        {
            registry.CouplingChanged -= coupling;
            registry.ObjectPlaced -= placed;
            registry.ObjectErased -= erased;
            registry.HistoryChanged -= history;
        });
    }

    private void _Wireframes(F registry)
    {
        const string p = WireframePrefix;
        _Do(p + nameof(F.SurfaceOpened), a => registry.SurfaceOpened(ReadString(a, 0), ReadString(a, 1), ReadString(a, 2)));
        _Do(p + nameof(F.SurfaceClosed), a => registry.SurfaceClosed(ReadString(a, 0)));
        _Do(p + nameof(F.Disconnect), a => registry.Disconnect(ReadString(a, 0)));
        _On(p + nameof(F.Couple), a => _Refusal(() => registry.Couple(ReadString(a, 0), ReadString(a, 1))));
        _On(p + nameof(F.ApplyHandEdit), a => registry.ApplyHandEdit(ReadString(a, 0), ReadArg<WireframeComponentEdit>(a, 1)));
        _On(p + nameof(F.History), a => registry.History(ReadString(a, 0)));
        _On(p + nameof(F.Revert), a => registry.Revert(ReadString(a, 0), ReadString(a, 1)));
        _On(p + nameof(F.EnsureComponentId), a => registry.EnsureComponentId(ReadString(a, 0), a[1].GetInt32()));
        _Do(p + nameof(F.HoldComponent), a => registry.HoldComponent(ReadString(a, 0), ReadString(a, 1)));
        _Do(p + nameof(F.ReleaseComponent), a => registry.ReleaseComponent(ReadString(a, 0), ReadString(a, 1)));
        _On(p + nameof(F.PeekText), a => registry.PeekText(ReadString(a, 0)));

        Action<string, string> text = (surfaceId, value) => _Publish(p + nameof(F.TextChanged), surfaceId, value);
        Action<WireframeCouplingChange> coupling = change => _Publish(p + nameof(F.CouplingChanged), change.SurfaceId, change);
        Action<string> history = surfaceId => _Publish(p + nameof(F.HistoryChanged), surfaceId);
        registry.TextChanged += text;
        registry.CouplingChanged += coupling;
        registry.HistoryChanged += history;
        _detach.Add(() =>
        {
            registry.TextChanged -= text;
            registry.CouplingChanged -= coupling;
            registry.HistoryChanged -= history;
        });
    }

    // A registry refuses a coupling it cannot take (another agent holds the surface) by throwing. Only JSON crosses
    // the channel, so the refusal crosses as its message and the UI half throws it again.
    private static string? _Refusal(Action couple)
    {
        try
        {
            couple();
            return null;
        }
        catch (InvalidOperationException exception)
        {
            return exception.Message;
        }
    }

    // The registries answer synchronously, so each handler does too: in-process the UI half gets a completed task.
    private void _On(string action, Func<JsonElement, object?> handle) =>
        _handles.Add(_host.Channel.Handle(action, (args, _) => Task.FromResult(JsonSerializer.SerializeToElement(handle(args), Json))));

    private void _Do(string action, Action<JsonElement> handle) =>
        _On(action, args =>
        {
            handle(args);
            return null;
        });

    private void _Publish(string name, params object?[] args) =>
        _host.Channel.Publish(name, JsonSerializer.SerializeToElement(args, Json));

    internal static string ReadString(JsonElement args, int index) =>
        args[index].GetString() ?? throw new JsonException($"Channel argument {index} is null where a string is required.");

    internal static T ReadArg<T>(JsonElement args, int index) =>
        args[index].Deserialize<T>(Json) ?? throw new JsonException($"Channel argument {index} is null where a {typeof(T).Name} is required.");
}
