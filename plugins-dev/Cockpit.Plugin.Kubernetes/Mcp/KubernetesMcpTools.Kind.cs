using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Cockpit.Plugin.Kubernetes.Mcp;

// AC-179: the kind-cluster lifecycle tools, exposed alongside the rest of mcp__cockpit-k8s__*. A cluster this creates
// is registered with the plugin's own KindClusterManager and shows up immediately in list_clusters/list_resources —
// no separate connection step. It is a disposable test environment: non-pinned clusters are torn down on session
// close, cockpit exit, or the configured TTL, never kept around for unsaved work the way a worktree is.
internal sealed partial class KubernetesMcpTools
{
    [McpServerTool(Name = "kind_create", ReadOnly = false, Destructive = false)]
    [Description("Creates a disposable local kind (Kubernetes-in-Docker) cluster and registers it with this plugin, so the other cockpit-k8s tools can reach it immediately with no manual kubeconfig step. Needs the kind binary and a container runtime (Docker or Podman) on this machine — a missing one is reported, not guessed at. Can take from ~30 seconds (node image already local) to several minutes (first pull, ~1.3 GB). The cluster is torn down automatically when this session closes, the cockpit exits, or its TTL expires, unless the operator pins it — this is a throwaway test environment, not a place for anything that must persist.")]
    public async Task<string> KindCreate(
        [Description("Your session id — the value of the COCKPIT_PANE_ID environment variable in this session.")] string session,
        [Description("A name for the cluster; becomes the kind cluster name and the kind-<name> kubeconfig context.")] string name,
        CancellationToken cancellationToken = default)
    {
        var decision = await gate.AuthorizeKindLifecycleAsync($"kind create cluster --name {name}", "k8s.kind.create", session);
        if (decision is { IsAllowed: false, DeniedReason: { } reason })
        {
            return McpText.Error(reason);
        }

        // AC-179 pitfall: the agent-supplied `session` is never the ownership record — the transport-verified
        // caller is, exactly as WorktreeTools.RemoveAsync uses McpRequestContext instead of its own `session` arg.
        var owner = host.CurrentMcpCallerPaneId ?? session;
        var (record, error) = await kindClusters.CreateAsync(name, owner, cancellationToken);
        if (record is null)
        {
            return McpText.Error(error!);
        }

        return McpText.Ok(new
        {
            ok = true,
            name = record.Name,
            kubeconfigPath = record.KubeconfigPath,
            contextName = $"kind-{record.Name}",
            notice = error,
        });
    }

    [McpServerTool(Name = "kind_list", ReadOnly = true)]
    [Description("Lists the kind clusters this plugin created — never one made outside the cockpit. Each entry has the name, age, owning session, kubeconfig path, whether it is pinned (kept regardless of session/TTL), and whether it is still actually running.")]
    public async Task<string> KindList(CancellationToken cancellationToken = default) =>
        McpText.Ok(new
        {
            ok = true,
            clusters = (await kindClusters.ListAsync(cancellationToken)).Select(entry => new
            {
                name = entry.Name,
                ageSeconds = (int)entry.Age.TotalSeconds,
                owner = entry.OwnerPaneId,
                kubeconfigPath = entry.KubeconfigPath,
                pinned = entry.IsPinned,
                running = entry.IsRunning,
            }),
        });

    [McpServerTool(Name = "kind_delete", ReadOnly = false, Destructive = true)]
    [Description("Deletes a kind cluster this plugin created: its containers, its kubeconfig file, and its Kubernetes-plugin cluster registration. Refuses a name that is not in this plugin's own registry — a cluster made outside the cockpit is never touched. Asks for approval every time, separately from create; approving a create does not approve a later delete.")]
    public async Task<string> KindDelete(
        [Description("Your session id — the value of the COCKPIT_PANE_ID environment variable in this session.")] string session,
        [Description("The cluster name, as returned by kind_create or kind_list.")] string name,
        CancellationToken cancellationToken = default)
    {
        var decision = await gate.AuthorizeKindLifecycleAsync($"kind delete cluster --name {name}", $"k8s.kind.delete:{name}", session);
        if (decision is { IsAllowed: false, DeniedReason: { } reason })
        {
            return McpText.Error(reason);
        }

        var (ok, error) = await kindClusters.DeleteAsync(name, cancellationToken);
        return ok ? McpText.Ok(new { ok = true }) : McpText.Error(error!);
    }
}
