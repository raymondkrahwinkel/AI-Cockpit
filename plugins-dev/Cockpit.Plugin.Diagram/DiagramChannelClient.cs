using System.Text.Json;
using Cockpit.Core.Abstractions.Diagrams;
using Cockpit.Core.Abstractions.Whiteboard;
using Cockpit.Core.Abstractions.Wireframe;
using Cockpit.Plugins.Abstractions.Channels;
using Cockpit.Plugins.Abstractions.UI;
using static Cockpit.Plugin.Diagram.DiagramChannel;
using D = Cockpit.Core.Abstractions.Diagrams.IDiagramAccessRegistry;
using W = Cockpit.Core.Abstractions.Whiteboard.IWhiteboardAccessRegistry;
using F = Cockpit.Core.Abstractions.Wireframe.IWireframeAccessRegistry;

namespace Cockpit.Plugin.Diagram;

// AC-1400 (F2.12): the UI half of Diagram's channel, one per open window. Each client mirrors the registry members
// its window used to take from the container, under the same names, so the window's own logic did not change with
// the route. A client subscribes when it is built and lets go when disposed.
internal abstract class SurfaceChannelClient(IPluginUiChannel channel) : IDisposable
{
    private readonly List<IDisposable> _subscriptions = [];
    private readonly Dictionary<(string Name, string SurfaceId), long> _lastSeq = [];
    private readonly Lock _gate = new();

    public void Dispose()
    {
        foreach (var subscription in _subscriptions)
        {
            subscription.Dispose();
        }
    }

    // ponytail: waits for the backend's answer. In-process the handler runs inline and the task is already complete,
    // so nothing blocks; a remote backend (F5/F6) needs these callers made async first.
    protected T? Invoke<T>(string action, params object?[] args) =>
        channel.InvokeAsync(action, JsonSerializer.SerializeToElement(args, Json)).GetAwaiter().GetResult().Deserialize<T>(Json);

    protected void Send(string action, params object?[] args) => Invoke<JsonElement>(action, args);

    // For the calls a registry refuses by throwing (see DiagramChannel._Refusal): the refusal is thrown again here.
    protected void InvokeOrRefuse(string action, params object?[] args)
    {
        if (Invoke<string>(action, args) is { } refusal)
        {
            throw new InvalidOperationException(refusal);
        }
    }

    // A state event (the whole text, the whole coupling, the whole proposal) makes an older one of the same name for
    // the same surface meaningless, so one that arrives after a newer one is dropped. A delta (an object placed) or
    // a nudge to re-read (history) is never dropped: skipping it would lose an edit the newer one does not carry.
    protected void Subscribe(string name, bool latestWins, Action<JsonElement> raise) =>
        _subscriptions.Add(channel.Subscribe(name, channelEvent =>
        {
            if (!latestWins || _IsNewest(channelEvent))
            {
                raise(channelEvent.Payload);
            }
        }));

    private bool _IsNewest(PluginChannelEvent channelEvent)
    {
        var key = (channelEvent.Name, ReadString(channelEvent.Payload, 0));
        lock (_gate)
        {
            if (_lastSeq.TryGetValue(key, out var last) && channelEvent.Seq <= last)
            {
                return false;
            }

            _lastSeq[key] = channelEvent.Seq;
            return true;
        }
    }
}

internal sealed class DiagramChannelClient : SurfaceChannelClient
{
    private const string P = DiagramPrefix;

    public DiagramChannelClient(IPluginUiChannel channel)
        : base(channel)
    {
        Subscribe(P + nameof(D.TextChanged), latestWins: true, a => TextChanged?.Invoke(ReadString(a, 0), ReadString(a, 1)));
        Subscribe(P + nameof(D.CouplingChanged), latestWins: true, a => CouplingChanged?.Invoke(ReadArg<DiagramCouplingChange>(a, 1)));
        Subscribe(P + nameof(D.ProposalChanged), latestWins: true, a => ProposalChanged?.Invoke(ReadString(a, 0), a[1].Deserialize<DiagramProposal>(Json)));
        Subscribe(P + nameof(D.HistoryChanged), latestWins: false, a => HistoryChanged?.Invoke(ReadString(a, 0)));
    }

    public event Action<string, string>? TextChanged;

    public event Action<DiagramCouplingChange>? CouplingChanged;

    public event Action<string, DiagramProposal?>? ProposalChanged;

    public event Action<string>? HistoryChanged;

    public void SurfaceOpened(string surfaceId, string name, string initialText) => Send(P + nameof(D.SurfaceOpened), surfaceId, name, initialText);

    public void SurfaceClosed(string surfaceId) => Send(P + nameof(D.SurfaceClosed), surfaceId);

    public void UpdateText(string surfaceId, string text) => Send(P + nameof(D.UpdateText), surfaceId, text);

    public void Disconnect(string surfaceId) => Send(P + nameof(D.Disconnect), surfaceId);

    public IReadOnlyList<DiagramSurfaceView> ListSurfaces(string sessionId) =>
        Invoke<List<DiagramSurfaceView>>(P + nameof(D.ListSurfaces), sessionId) ?? [];

    public bool IsCoupledByAnother(string sessionId, string surfaceId) => Invoke<bool>(P + nameof(D.IsCoupledByAnother), sessionId, surfaceId);

    public void Couple(string sessionId, string surfaceId) => InvokeOrRefuse(P + nameof(D.Couple), sessionId, surfaceId);

    public string? ApplyHandEdit(string surfaceId, DiagramHandEdit edit) => Invoke<string>(P + nameof(D.ApplyHandEdit), surfaceId, edit);

    public DiagramEditSupport EditSupport(string surfaceId) =>
        Invoke<DiagramEditSupport>(P + nameof(D.EditSupport), surfaceId) ?? new DiagramEditSupport(DiagramEditDialect.Unsupported, null);

    public IReadOnlyList<DiagramErAttribute> EntityAttributes(string surfaceId, string entity) =>
        Invoke<List<DiagramErAttribute>>(P + nameof(D.EntityAttributes), surfaceId, entity) ?? [];

    public IReadOnlyList<DiagramHistoryEntry> History(string surfaceId) =>
        Invoke<List<DiagramHistoryEntry>>(P + nameof(D.History), surfaceId) ?? [];

    public string? Revert(string surfaceId, string entryId) => Invoke<string>(P + nameof(D.Revert), surfaceId, entryId);

    public void HoldObject(string surfaceId, string objectId) => Send(P + nameof(D.HoldObject), surfaceId, objectId);

    public void ReleaseObject(string surfaceId, string objectId) => Send(P + nameof(D.ReleaseObject), surfaceId, objectId);

    public bool ResolveProposal(string surfaceId, IReadOnlySet<int> acceptedBlocks) => Invoke<bool>(P + nameof(D.ResolveProposal), surfaceId, acceptedBlocks);

    public bool DiscardProposal(string surfaceId) => Invoke<bool>(P + nameof(D.DiscardProposal), surfaceId);
}

internal sealed class WhiteboardChannelClient : SurfaceChannelClient
{
    private const string P = WhiteboardPrefix;

    public WhiteboardChannelClient(IPluginUiChannel channel)
        : base(channel)
    {
        Subscribe(P + nameof(W.CouplingChanged), latestWins: true, a => CouplingChanged?.Invoke(ReadArg<WhiteboardCouplingChange>(a, 1)));
        Subscribe(P + nameof(W.ObjectPlaced), latestWins: false, a => ObjectPlaced?.Invoke(ReadString(a, 0), ReadString(a, 1), ReadArg<WhiteboardPlacement>(a, 2)));
        Subscribe(P + nameof(W.ObjectErased), latestWins: false, a => ObjectErased?.Invoke(ReadString(a, 0), ReadString(a, 1)));
        Subscribe(P + nameof(W.HistoryChanged), latestWins: false, a => HistoryChanged?.Invoke(ReadString(a, 0)));
    }

    public event Action<WhiteboardCouplingChange>? CouplingChanged;

    public event Action<string, string, WhiteboardPlacement>? ObjectPlaced;

    public event Action<string, string>? ObjectErased;

    public event Action<string>? HistoryChanged;

    public void SurfaceOpened(string surfaceId, string name, byte[] initialSnapshotPng) => Send(P + nameof(W.SurfaceOpened), surfaceId, name, initialSnapshotPng);

    public void SurfaceClosed(string surfaceId) => Send(P + nameof(W.SurfaceClosed), surfaceId);

    public void UpdateSnapshot(string surfaceId, byte[] snapshotPng) => Send(P + nameof(W.UpdateSnapshot), surfaceId, snapshotPng);

    public void Disconnect(string surfaceId) => Send(P + nameof(W.Disconnect), surfaceId);

    public void Couple(string sessionId, string surfaceId) => InvokeOrRefuse(P + nameof(W.Couple), sessionId, surfaceId);

    public void Grant(string sessionId, string surfaceId, WhiteboardCapability capability = WhiteboardCapability.Read) =>
        InvokeOrRefuse(P + nameof(W.Grant), sessionId, surfaceId, capability);

    public IReadOnlyList<WhiteboardHistoryEntry> History(string surfaceId) =>
        Invoke<List<WhiteboardHistoryEntry>>(P + nameof(W.History), surfaceId) ?? [];

    public string? Revert(string surfaceId, string entryId) => Invoke<string>(P + nameof(W.Revert), surfaceId, entryId);
}

internal sealed class WireframeChannelClient : SurfaceChannelClient
{
    private const string P = WireframePrefix;

    public WireframeChannelClient(IPluginUiChannel channel)
        : base(channel)
    {
        Subscribe(P + nameof(F.TextChanged), latestWins: true, a => TextChanged?.Invoke(ReadString(a, 0), ReadString(a, 1)));
        Subscribe(P + nameof(F.CouplingChanged), latestWins: true, a => CouplingChanged?.Invoke(ReadArg<WireframeCouplingChange>(a, 1)));
        Subscribe(P + nameof(F.HistoryChanged), latestWins: false, a => HistoryChanged?.Invoke(ReadString(a, 0)));
    }

    public event Action<string, string>? TextChanged;

    public event Action<WireframeCouplingChange>? CouplingChanged;

    public event Action<string>? HistoryChanged;

    public void SurfaceOpened(string surfaceId, string name, string initialText) => Send(P + nameof(F.SurfaceOpened), surfaceId, name, initialText);

    public void SurfaceClosed(string surfaceId) => Send(P + nameof(F.SurfaceClosed), surfaceId);

    public void Disconnect(string surfaceId) => Send(P + nameof(F.Disconnect), surfaceId);

    public void Couple(string sessionId, string surfaceId) => InvokeOrRefuse(P + nameof(F.Couple), sessionId, surfaceId);

    public string? ApplyHandEdit(string surfaceId, WireframeComponentEdit edit) => Invoke<string>(P + nameof(F.ApplyHandEdit), surfaceId, edit);

    public IReadOnlyList<WireframeHistoryEntry> History(string surfaceId) =>
        Invoke<List<WireframeHistoryEntry>>(P + nameof(F.History), surfaceId) ?? [];

    public string? Revert(string surfaceId, string entryId) => Invoke<string>(P + nameof(F.Revert), surfaceId, entryId);

    public string? EnsureComponentId(string surfaceId, int line) => Invoke<string>(P + nameof(F.EnsureComponentId), surfaceId, line);

    public void HoldComponent(string surfaceId, string componentId) => Send(P + nameof(F.HoldComponent), surfaceId, componentId);

    public void ReleaseComponent(string surfaceId, string componentId) => Send(P + nameof(F.ReleaseComponent), surfaceId, componentId);

    public string? PeekText(string surfaceId) => Invoke<string>(P + nameof(F.PeekText), surfaceId);
}
