using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.Plugin.Proxmox.Contracts;
using Cockpit.Plugin.Proxmox.Engine;
using Cockpit.Plugin.Proxmox.Mcp;
using Cockpit.Plugin.Proxmox.Security;
using Cockpit.Plugin.Proxmox.Settings;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Proxmox;

// Proxmox VE plugin entry point (AC-1038). Registers a Proxmox target and injects a cockpit-proxmox MCP server;
// the plugin talks to the REST API itself and keeps the token, gating every call through `ProxmoxAccessGate`.
// Sibling of the Docker and Kubernetes plugins.
//
// AC-1394: the backend part. The overview workspace and the settings view (ProxmoxUi, in Ui/) ask what the API
// says — nodes/VMs/LXC containers/storage, and start/shutdown/stop — over the plugin's channel; this part answers,
// keeping the gate and the engine here so neither ever crosses into the UI assembly.
public sealed class ProxmoxPlugin : ICockpitPlugin
{
    public PluginMetadata Metadata { get; } = new(
        Id: "proxmox",
        DisplayName: "Proxmox VE",
        Author: "Cockpit",
        Description: "Register a Proxmox VE host or cluster and give agents scoped, human-approved access to its nodes, VMs and LXC containers through a cockpit-proxmox MCP server. The plugin talks to the Proxmox REST API itself and keeps the API token — an agent never gets it. Connecting asks for consent once, and every change asks afresh with the literal action shown and is never remembered. Rollback and delete are off until you turn them on.");

    private ProxmoxEngine? _engine;
    private readonly List<IDisposable> _handlers = [];

    public void ConfigureServices(IServiceCollection services)
    {
    }

    public void Initialize(ICockpitHost host)
    {
        var settings = new ProxmoxSettings(host.Storage);
        var engine = new ProxmoxEngine(settings);
        _engine = engine;
        var gate = new ProxmoxAccessGate(host);
        var tools = new ProxmoxMcpTools(settings, gate, engine);

        _ = host.AddMcpEndpoint("cockpit-proxmox", tools, isEnabled: () => settings.McpEnabled);

        _handlers.Add(host.Channel.Handle(ProxmoxChannel.Overview, (payload, cancellationToken) => _AnswerOverviewAsync(payload, settings, gate, engine, cancellationToken)));
        _handlers.Add(host.Channel.Handle(ProxmoxChannel.StartGuest, (payload, cancellationToken) =>
            _AnswerGuestActionAsync(payload, settings, gate, engine, cancellationToken, ProxmoxActionText.StartVm, ProxmoxActionText.StartLxc, (selected, node, vmId, ct) => selected.StartVmAsync(node, vmId, ct), (selected, node, vmId, ct) => selected.StartLxcAsync(node, vmId, ct))));
        _handlers.Add(host.Channel.Handle(ProxmoxChannel.ShutdownGuest, (payload, cancellationToken) =>
            _AnswerGuestActionAsync(payload, settings, gate, engine, cancellationToken, ProxmoxActionText.ShutdownVm, ProxmoxActionText.ShutdownLxc, (selected, node, vmId, ct) => selected.ShutdownVmAsync(node, vmId, ct), (selected, node, vmId, ct) => selected.ShutdownLxcAsync(node, vmId, ct))));
        _handlers.Add(host.Channel.Handle(ProxmoxChannel.StopGuest, (payload, cancellationToken) =>
            _AnswerGuestActionAsync(payload, settings, gate, engine, cancellationToken, ProxmoxActionText.StopVm, ProxmoxActionText.StopLxc, (selected, node, vmId, ct) => selected.StopVmAsync(node, vmId, ct), (selected, node, vmId, ct) => selected.StopLxcAsync(node, vmId, ct))));
        _handlers.Add(host.Channel.Handle(ProxmoxChannel.FetchFingerprint, async (payload, cancellationToken) =>
        {
            var request = payload.Deserialize<ProxmoxFingerprintRequest>(ProxmoxChannel.Json)
                ?? throw new InvalidOperationException("A fingerprint request needs a host and port.");
            // A host that never answers would hold the connection open; ten seconds is ample for one TLS handshake.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                var (fingerprint, error) = await ProxmoxCertificateProbe.FetchFingerprintAsync(request.Host, request.Port, timeout.Token).ConfigureAwait(false);
                return _Serialize(new ProxmoxFingerprintAnswer(fingerprint, error));
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return _Serialize(new ProxmoxFingerprintAnswer(null, $"{request.Host}:{request.Port} did not present a certificate within ten seconds."));
            }
        }));
        _handlers.Add(host.Channel.Handle(ProxmoxChannel.InvalidateTarget, (_, _) =>
        {
            // A settings save may have changed the target or its trusted certificate; drop the cached client so
            // the next call rebuilds it. Synchronous, so no cancellation token to honor.
            engine.Invalidate();
            return Task.FromResult(_Serialize(new ProxmoxEmptyRequest()));
        }));
    }

    public void Dispose()
    {
        foreach (var handler in _handlers)
        {
            handler.Dispose();
        }

        _handlers.Clear();
        _engine?.Dispose();
    }

    private static async Task<JsonElement> _AnswerOverviewAsync(JsonElement payload, ProxmoxSettings settings, ProxmoxAccessGate gate, IProxmoxEngine engine, CancellationToken cancellationToken)
    {
        var request = payload.Deserialize<ProxmoxOverviewRequest>(ProxmoxChannel.Json) ?? new ProxmoxOverviewRequest(null);
        ProxmoxServer server;
        try
        {
            server = settings.ResolveServer(request.Server);
        }
        catch (Exception ex)
        {
            return _Serialize(new ProxmoxOverviewAnswer(null, _FailureMessage(ex), null, null, null, null, null));
        }

        var selected = engine.ForServer(server);
        var decision = await gate.AuthorizeConnectionAsync($"show the Proxmox overview for server \"{server.Name}\"", paneId: null);
        if (decision is { IsAllowed: false, DeniedReason: { } reason })
        {
            return _Serialize(new ProxmoxOverviewAnswer(reason, null, null, null, null, null, null));
        }

        try
        {
            var clusterTask = selected.GetClusterInfoAsync(cancellationToken);
            var nodesTask = selected.ListNodesAsync(cancellationToken);
            var vmsTask = selected.ListVmsAsync(cancellationToken);
            var lxcTask = selected.ListLxcAsync(cancellationToken);
            var storageTask = selected.ListStorageAsync(cancellationToken);
            await Task.WhenAll(clusterTask, nodesTask, vmsTask, lxcTask, storageTask);

            var cluster = clusterTask.Result;
            var answer = new ProxmoxOverviewAnswer(
                DeniedReason: null,
                ErrorMessage: null,
                Cluster: new ProxmoxClusterData(cluster.IsCluster, cluster.Name, cluster.Quorate, cluster.NodeCount),
                Nodes: nodesTask.Result.Select(node => new ProxmoxNodeData(node.Node, node.Status, node.CpuUsage, node.MaxCpu, node.MemUsed, node.MemMax, node.Uptime)).ToList(),
                Vms: vmsTask.Result.Select(_MapGuest).ToList(),
                Lxc: lxcTask.Result.Select(_MapGuest).ToList(),
                Storage: storageTask.Result.Select(pool => new ProxmoxStorageData(pool.Storage, pool.Node, pool.Type, pool.TotalBytes, pool.UsedBytes, pool.Enabled)).ToList());
            return _Serialize(answer);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return _Serialize(new ProxmoxOverviewAnswer(null, _FailureMessage(ex), null, null, null, null, null));
        }
    }

    private static async Task<JsonElement> _AnswerGuestActionAsync(
        JsonElement payload,
        ProxmoxSettings settings,
        ProxmoxAccessGate gate,
        IProxmoxEngine engine,
        CancellationToken cancellationToken,
        Func<string, string, string> vmOperationText,
        Func<string, string, string> lxcOperationText,
        Func<IProxmoxEngine, string, string, CancellationToken, Task<ProxmoxTaskOutcome>> vmAction,
        Func<IProxmoxEngine, string, string, CancellationToken, Task<ProxmoxTaskOutcome>> lxcAction)
    {
        var request = payload.Deserialize<ProxmoxGuestActionRequest>(ProxmoxChannel.Json)
            ?? throw new ArgumentException("The request names no guest.", nameof(payload));

        ProxmoxServer server;
        try
        {
            server = settings.ResolveServer(request.Server);
        }
        catch (Exception ex)
        {
            return _Serialize(new ProxmoxActionAnswer(null, _FailureMessage(ex), null));
        }

        var operation = $"{(request.IsLxc ? lxcOperationText(request.Node, request.VmId) : vmOperationText(request.Node, request.VmId))} on Proxmox server \"{server.Name}\"";
        var decision = await gate.AuthorizeMutationAsync(operation, paneId: null);
        if (decision is { IsAllowed: false, DeniedReason: { } reason })
        {
            return _Serialize(new ProxmoxActionAnswer(reason, null, null));
        }

        try
        {
            var outcome = request.IsLxc
                ? await lxcAction(engine.ForServer(server), request.Node, request.VmId, cancellationToken)
                : await vmAction(engine.ForServer(server), request.Node, request.VmId, cancellationToken);
            return _Serialize(new ProxmoxActionAnswer(null, null, new ProxmoxTaskOutcomeData(outcome.Upid, outcome.IsSuccess, outcome.ExitStatus, outcome.TimedOut)));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return _Serialize(new ProxmoxActionAnswer(null, _FailureMessage(ex), null));
        }
    }

    private static ProxmoxGuestData _MapGuest(ProxmoxGuest guest) =>
        new(guest.VmId, guest.Name, guest.Node, guest.Status, guest.MaxMem, guest.MaxDisk, guest.MaxCpu, guest.Uptime);

    // Never leak a stack trace or raw exception text to the UI part (mirroring the MCP tools' own `_Failure`) — a
    // `ProxmoxApiException` is already a readable, safe message; anything else is reduced to its type name.
    private static string _FailureMessage(Exception ex) =>
        ex is ProxmoxApiException apiEx ? apiEx.Message : $"The Proxmox request failed ({ex.GetType().Name}).";

    private static JsonElement _Serialize<T>(T value) => JsonSerializer.SerializeToElement(value, ProxmoxChannel.Json);
}
