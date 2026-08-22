---
title: Clusters
order: 10
summary: Registering a cluster, picking a context, and the setting that silently follows kubectl elsewhere.
icon: ☸️
---

Each row in this plugin's settings is one cluster: a kubeconfig, a context picked from it, which namespaces
an agent may reach without asking each time, and a set of capability toggles that are off until you turn
them on. The plugin holds the kubeconfig itself — an agent never sees it, only the result of a gated MCP
call.

## Adding a cluster {#adding-a-cluster}

A cluster needs a label (agents and consent prompts refer to it by this name — a blank label is refused on
save) and a source for its kubeconfig: either a file path, or a kubeconfig pasted directly into the settings
form.

## Kubeconfig file vs. pasted kubeconfig {#kubeconfig-source}

**File path** (e.g. `~/.kube/config`) is read live on every connection — if the file changes on disk, the
plugin picks that up the next time it connects (a *rotated static token* in the file is only picked up after
a settings save invalidates the cached client; an exec-auth context re-runs its credential plugin on every
call regardless). **Pasted kubeconfig** is stored once, encrypted at rest under the secret layer, and stays
exactly as pasted until you replace it — a later change to the source file has no effect on it. Setting a
file path always wins: if a path is present, any previously pasted kubeconfig for that row is dropped rather
than kept as a fallback.

## Picking a context {#context}

Press **Load contexts** (or **Browse…** to pick the file, which loads contexts automatically) to populate
the context dropdown from the kubeconfig. Leaving it on **(current-context)** does not pin anything — it
means "use whatever this kubeconfig's `current-context` says," resolved fresh on every connection.

## When the context silently follows kubectl elsewhere {#context-fallback}

This is the pitfall worth knowing before you register a cluster against your everyday `~/.kube/config`: if
the context is left on **(current-context)**, this plugin connects to whatever context that file currently
points at — which is the same file `kubectl config use-context` changes. Switch clusters at the terminal
for unrelated work, and the next agent call through this plugin silently follows you there, without
anything in this plugin's own UI having changed. Pick an explicit context from the dropdown for any cluster
you want an agent connecting to reliably, and reserve **(current-context)** for a kubeconfig you keep
dedicated to this plugin.

## Namespaces and the extra capabilities {#capabilities}

Namespaces listed in **Allowed namespaces** are free to read; anything outside that list asks each session,
reads included. **Cluster-scoped resources**, **exec**, and **port-forward** are each off by default and
each reach past the namespace boundary in their own way — a node list, a shell inside a pod, or a tunnel
into the cluster network — so turn on only the ones you actually need for this cluster.

## Exec-auth kubeconfigs {#exec-auth}

A kubeconfig whose context authenticates through an exec credential plugin (common with managed clusters —
`aws eks get-token`, `gke-gcloud-auth-plugin`, and similar) runs that external command every time the
plugin connects. The row shows a warning when it detects this, but the run itself is not gated the way an
agent's exec-into-a-pod is — only use a kubeconfig here whose exec plugin you trust.
