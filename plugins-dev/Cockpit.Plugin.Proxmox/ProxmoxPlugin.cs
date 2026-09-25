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

        _handlers.Add(host.Channel.Handle(ProxmoxChannel.Overview, (_, cancellationToken) => _AnswerOverviewAsync(gate, engine, cancellationToken)));
        _handlers.Add(host.Channel.Handle(ProxmoxChannel.StartGuest, (payload, cancellationToken) =>
            _AnswerGuestActionAsync(payload, gate, cancellationToken, ProxmoxActionText.StartVm, ProxmoxActionText.StartLxc, engine.StartVmAsync, engine.StartLxcAsync)));
        _handlers.Add(host.Channel.Handle(ProxmoxChannel.ShutdownGuest, (payload, cancellationToken) =>
            _AnswerGuestActionAsync(payload, gate, cancellationToken, ProxmoxActionText.ShutdownVm, ProxmoxActionText.ShutdownLxc, engine.ShutdownVmAsync, engine.ShutdownLxcAsync)));
        _handlers.Add(host.Channel.Handle(ProxmoxChannel.StopGuest, (payload, cancellationToken) =>
            _AnswerGuestActionAsync(payload, gate, cancellationToken, ProxmoxActionText.StopVm, ProxmoxActionText.StopLxc, engine.StopVmAsync, engine.StopLxcAsync)));
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

    private static async Task<JsonElement> _AnswerOverviewAsync(ProxmoxAccessGate gate, IProxmoxEngine engine, CancellationToken cancellationToken)
    {
        var decision = await gate.AuthorizeConnectionAsync("show the Proxmox overview", paneId: null);
        if (decision is { IsAllowed: false, DeniedReason: { } reason })
        {
            return _Serialize(new ProxmoxOverviewAnswer(reason, null, null, null, null, null, null));
        }

        try
        {
            var clusterTask = engine.GetClusterInfoAsync(cancellationToken);
            var nodesTask = engine.ListNodesAsync(cancellationToken);
            var vmsTask = engine.ListVmsAsync(cancellationToken);
            var lxcTask = engine.ListLxcAsync(cancellationToken);
            var storageTask = engine.ListStorageAsync(cancellationToken);
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
        ProxmoxAccessGate gate,
        CancellationToken cancellationToken,
        Func<string, string, string> vmOperationText,
        Func<string, string, string> lxcOperationText,
        Func<string, string, CancellationToken, Task<ProxmoxTaskOutcome>> vmAction,
        Func<string, string, CancellationToken, Task<ProxmoxTaskOutcome>> lxcAction)
    {
        var request = payload.Deserialize<ProxmoxGuestActionRequest>(ProxmoxChannel.Json)
            ?? throw new ArgumentException("The request names no guest.", nameof(payload));

        var operation = request.IsLxc ? lxcOperationText(request.Node, request.VmId) : vmOperationText(request.Node, request.VmId);
        var decision = await gate.AuthorizeMutationAsync(operation, paneId: null);
        if (decision is { IsAllowed: false, DeniedReason: { } reason })
        {
            return _Serialize(new ProxmoxActionAnswer(reason, null, null));
        }

        try
        {
            var outcome = request.IsLxc
                ? await lxcAction(request.Node, request.VmId, cancellationToken)
                : await vmAction(request.Node, request.VmId, cancellationToken);
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
