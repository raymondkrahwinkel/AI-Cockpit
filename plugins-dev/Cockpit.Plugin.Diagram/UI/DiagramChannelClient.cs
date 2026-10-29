using System.Text.Json;
using Avalonia.Threading;
using Cockpit.Core.Abstractions.Diagrams;
using Cockpit.Core.Abstractions.Whiteboard;
using Cockpit.Core.Abstractions.Wireframe;
using Cockpit.Plugin.Diagram.Whiteboard;
using Cockpit.Plugin.Diagram.Whiteboard.Model;
using Cockpit.Plugin.Diagram.Wireframe;
using Cockpit.Plugins.Abstractions.Channels;
using Cockpit.Plugins.Abstractions.UI;
using static Cockpit.Plugin.Diagram.DiagramChannelContract;
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
    private Task _tail = Task.CompletedTask;

    public IPluginUiChannel Channel => channel;

    public void Dispose()
    {
        foreach (var subscription in _subscriptions)
        {
            subscription.Dispose();
        }
    }

    // Whether the backend has the registry behind `prefix`; without it a window is in the "older host" state.
    protected static async Task<bool> ServesAsync(IPluginUiChannel? channel, string prefix)
    {
        if (channel is null)
        {
            return false;
        }

        try
        {
            await channel.InvokeAsync(prefix + Served, default).ConfigureAwait(false);
            return true;
        }
        catch (PluginChannelUnknownActionException)
        {
            return false;
        }
    }

    // AC-1891: one call at a time, in the order they were made. The hub runs every handler on its own pool thread,
    // so two quick sends (UpdateText, UpdateText) would otherwise race to the registry. A backend that went away
    // (the plugin disabled under an open window) leaves the window inert, as without a registry, never a crash.
    protected Task<T?> InvokeAsync<T>(string action, params object?[] args)
    {
        var payload = JsonSerializer.SerializeToElement(args, Json);
        lock (_gate)
        {
            var call = _AfterAsync(_tail, action, payload);
            _tail = call;
            return _ReadAsync<T>(call);
        }
    }

    // Fire and forget, still in order. A failed send has no caller left to tell; the next read shows the state.
    protected void Send(string action, params object?[] args) => _ = _SwallowAsync(InvokeAsync<JsonElement>(action, args));

    // For the calls a registry refuses by throwing (see DiagramChannel._Refusal): the refusal is thrown again here.
    protected async Task InvokeOrRefuseAsync(string action, params object?[] args)
    {
        if (await InvokeAsync<string>(action, args).ConfigureAwait(false) is { } refusal)
        {
            throw new InvalidOperationException(refusal);
        }
    }

    // Waits for the call before it, whatever became of that one: its own caller hears about its failure.
    private async Task<JsonElement?> _AfterAsync(Task previous, string action, JsonElement payload)
    {
        await previous.ContinueWith(static _ => { }, TaskScheduler.Default).ConfigureAwait(false);
        try
        {
            return await channel.InvokeAsync(action, payload).ConfigureAwait(false);
        }
        catch (PluginChannelUnknownActionException)
        {
            return null;
        }
    }

    private static async Task<T?> _ReadAsync<T>(Task<JsonElement?> call) =>
        await call.ConfigureAwait(false) is { } answer ? answer.Deserialize<T>(Json) : default;

    private static async Task _SwallowAsync(Task call)
    {
        try
        {
            await call.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // See Send.
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

    public static Task<bool> ServesAsync(IPluginUiChannel? channel) => ServesAsync(channel, P);

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

    public async Task<IReadOnlyList<DiagramSurfaceView>> ListSurfacesAsync(string sessionId) =>
        await InvokeAsync<List<DiagramSurfaceView>>(P + nameof(D.ListSurfaces), sessionId) ?? [];

    public Task<bool> IsCoupledByAnotherAsync(string sessionId, string surfaceId) => InvokeAsync<bool>(P + nameof(D.IsCoupledByAnother), sessionId, surfaceId);

    public Task CoupleAsync(string sessionId, string surfaceId) => InvokeOrRefuseAsync(P + nameof(D.Couple), sessionId, surfaceId);

    public Task<string?> ApplyHandEditAsync(string surfaceId, DiagramHandEdit edit) => InvokeAsync<string>(P + nameof(D.ApplyHandEdit), surfaceId, edit);

    public async Task<DiagramEditSupport> EditSupportAsync(string surfaceId) =>
        await InvokeAsync<DiagramEditSupport>(P + nameof(D.EditSupport), surfaceId) ?? new DiagramEditSupport(DiagramEditDialect.Unsupported, null);

    public async Task<IReadOnlyList<DiagramErAttribute>> EntityAttributesAsync(string surfaceId, string entity) =>
        await InvokeAsync<List<DiagramErAttribute>>(P + nameof(D.EntityAttributes), surfaceId, entity) ?? [];

    public async Task<IReadOnlyList<DiagramHistoryEntry>> HistoryAsync(string surfaceId) =>
        await InvokeAsync<List<DiagramHistoryEntry>>(P + nameof(D.History), surfaceId) ?? [];

    public Task<string?> RevertAsync(string surfaceId, string entryId) => InvokeAsync<string>(P + nameof(D.Revert), surfaceId, entryId);

    public void HoldObject(string surfaceId, string objectId) => Send(P + nameof(D.HoldObject), surfaceId, objectId);

    public void ReleaseObject(string surfaceId, string objectId) => Send(P + nameof(D.ReleaseObject), surfaceId, objectId);

    public void ResolveProposal(string surfaceId, IReadOnlySet<int> acceptedBlocks) => Send(P + nameof(D.ResolveProposal), surfaceId, acceptedBlocks);

    public void DiscardProposal(string surfaceId) => Send(P + nameof(D.DiscardProposal), surfaceId);
}

internal sealed class WhiteboardChannelClient : SurfaceChannelClient
{
    private const string P = WhiteboardPrefix;

    public static Task<bool> ServesAsync(IPluginUiChannel? channel) => ServesAsync(channel, P);

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

    public Task CoupleAsync(string sessionId, string surfaceId) => InvokeOrRefuseAsync(P + nameof(W.Couple), sessionId, surfaceId);

    public Task GrantAsync(string sessionId, string surfaceId, WhiteboardCapability capability = WhiteboardCapability.Read) =>
        InvokeOrRefuseAsync(P + nameof(W.Grant), sessionId, surfaceId, capability);

    public async Task<IReadOnlyList<WhiteboardHistoryEntry>> HistoryAsync(string surfaceId) =>
        await InvokeAsync<List<WhiteboardHistoryEntry>>(P + nameof(W.History), surfaceId) ?? [];

    public Task<string?> RevertAsync(string surfaceId, string entryId) => InvokeAsync<string>(P + nameof(W.Revert), surfaceId, entryId);
}

internal sealed class WireframeChannelClient : SurfaceChannelClient
{
    private const string P = WireframePrefix;

    public static Task<bool> ServesAsync(IPluginUiChannel? channel) => ServesAsync(channel, P);

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

    public Task CoupleAsync(string sessionId, string surfaceId) => InvokeOrRefuseAsync(P + nameof(F.Couple), sessionId, surfaceId);

    public Task<string?> ApplyHandEditAsync(string surfaceId, WireframeComponentEdit edit) => InvokeAsync<string>(P + nameof(F.ApplyHandEdit), surfaceId, edit);

    public async Task<IReadOnlyList<WireframeHistoryEntry>> HistoryAsync(string surfaceId) =>
        await InvokeAsync<List<WireframeHistoryEntry>>(P + nameof(F.History), surfaceId) ?? [];

    public Task<string?> RevertAsync(string surfaceId, string entryId) => InvokeAsync<string>(P + nameof(F.Revert), surfaceId, entryId);

    public Task<string?> EnsureComponentIdAsync(string surfaceId, int line) => InvokeAsync<string>(P + nameof(F.EnsureComponentId), surfaceId, line);

    public void HoldComponent(string surfaceId, string componentId) => Send(P + nameof(F.HoldComponent), surfaceId, componentId);

    public void ReleaseComponent(string surfaceId, string componentId) => Send(P + nameof(F.ReleaseComponent), surfaceId, componentId);

    public Task<string?> PeekTextAsync(string surfaceId) => InvokeAsync<string>(P + nameof(F.PeekText), surfaceId);
}

// The UI part's end of an agent's open_* tool: the backend asked the operator's consent, this opens the window.
// Attaching tells the backend a UI part is listening, so without one the tool answers opened: false instead.
internal sealed class SurfaceWindowOpener : IDisposable
{
    private readonly IPluginUiChannel _channel;
    private readonly IDisposable _subscription;

    public SurfaceWindowOpener(ICockpitUiHost host, IPluginUiChannel channel)
    {
        _channel = channel;
        _subscription = channel.Subscribe(OpenSurface, channelEvent => _Open(host, channel, channelEvent.Payload));
        Attached = _TellAsync(AttachUi);
    }

    // Done once the backend knows a UI part listens; until then an open_* tool still answers opened: false.
    public Task Attached { get; }

    // After the attach, never before it, so a quick dispose cannot leave the backend believing a UI part listens.
    public void Dispose()
    {
        _subscription.Dispose();
        _ = Attached.ContinueWith(_ => _TellAsync(DetachUi), TaskScheduler.Default).Unwrap();
    }

    private async Task _TellAsync(string action)
    {
        try
        {
            await _channel.InvokeAsync(action, default).ConfigureAwait(false);
        }
        catch (PluginChannelUnknownActionException)
        {
            // The backend part is already gone, and with it the attachment this would have withdrawn.
        }
    }

    // Posted: the event arrives on the tool's thread. The window couples to the calling session as it always did.
    private static void _Open(ICockpitUiHost host, IPluginUiChannel channel, JsonElement payload)
    {
        var surfaceId = ReadString(payload, 0);
        var kind = ReadString(payload, 1);
        var title = ReadString(payload, 2);
        var source = payload[3].GetString() ?? "";
        var caller = ReadString(payload, 4);
        Dispatcher.UIThread.Post(() => _ = kind switch
        {
            WhiteboardPrefix => WhiteboardWindow.OpenAsync(host, channel, new WhiteboardDocument(surfaceId, title), caller),
            WireframePrefix => WireframeWindow.OpenAsync(host, channel, new WireframeDocument(surfaceId, title, source), caller),
            DiagramPrefix => DiagramWindow.OpenAsync(host, channel, new DiagramDocument(surfaceId, title, source), caller),
            _ => Task.CompletedTask,
        });
    }
}
