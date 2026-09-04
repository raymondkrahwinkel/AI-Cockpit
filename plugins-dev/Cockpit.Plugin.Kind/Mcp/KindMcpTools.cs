using System.ComponentModel;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugin.Kind.Security;
using ModelContextProtocol.Server;

namespace Cockpit.Plugin.Kind.Mcp;

// AC-179: the kind-cluster lifecycle tools, served as mcp__cockpit-kind__* since AC-1079 split them out of the
// Kubernetes plugin. Disposable: non-pinned clusters are torn down on session close, cockpit exit, or TTL, never
// kept for unsaved work like a worktree is.
internal sealed class KindMcpTools(KindConsentGate gate, KindClusterManager kindClusters, ICockpitHost host)
{
    [McpServerTool(Name = "kind_create", ReadOnly = false, Destructive = false)]
    [Description("Creates a disposable local kind (Kubernetes-in-Docker) cluster and returns the kubeconfig path and context name to reach it with. With the Kubernetes plugin installed it also registers there, so the cockpit-k8s tools reach it with no manual step; without that plugin the answer says so and you use the kubeconfig directly. Needs the kind binary and a container runtime (Docker or Podman) on this machine — a missing one is reported, not guessed at. Can take from ~30 seconds (node image already local) to several minutes (first pull, ~1.3 GB). The cluster is torn down automatically when this session closes, the cockpit exits, or its TTL expires, unless the operator pins it — this is a throwaway test environment, not a place for anything that must persist.")]
    public async Task<string> KindCreate(
        [Description("Your session id — the value of the COCKPIT_PANE_ID environment variable in this session.")] string session,
        [Description("A name for the cluster; becomes the kind cluster name and the kind-<name> kubeconfig context.")] string name,
        CancellationToken cancellationToken = default)
    {
        if (await gate.AuthorizeAsync($"kind create cluster --name {name}", "kind.create", session) is { } denied)
        {
            return McpText.Error(denied);
        }

        // AC-179 pitfall: the agent-supplied `session` is never the ownership record — the transport-verified
        // caller is, same fallback shape as WorktreeTools.CreateAsync's `McpRequestContext.CurrentPaneId ?? session`.
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
    [Description("Deletes a kind cluster this plugin created: its containers, its kubeconfig file, and its Kubernetes-plugin registration if there is one. Refuses a name that is not in this plugin's own registry — a cluster made outside the cockpit is never touched. Asks for approval every time, separately from create; approving a create does not approve a later delete.")]
    public async Task<string> KindDelete(
        [Description("Your session id — the value of the COCKPIT_PANE_ID environment variable in this session.")] string session,
        [Description("The cluster name, as returned by kind_create or kind_list.")] string name,
        CancellationToken cancellationToken = default)
    {
        if (await gate.AuthorizeAsync($"kind delete cluster --name {name}", $"kind.delete:{name}", session) is { } denied)
        {
            return McpText.Error(denied);
        }

        var (ok, error) = await kindClusters.DeleteAsync(name, cancellationToken);
        return ok ? McpText.Ok(new { ok = true }) : McpText.Error(error!);
    }
}
