---
title: Kind clusters
order: 10
summary: Disposable local Kubernetes clusters an agent spins up, and the three ways one gets torn down again.
icon: 📦
---

A kind cluster is a throwaway Kubernetes cluster running in containers on this machine. An agent creates one
with `kind_create`, sees what exists with `kind_list`, and removes one with `kind_delete`. Nothing here is
meant to persist: treat a kind cluster as a test environment that can disappear, never as somewhere to put
work you have not saved elsewhere.

## What you need on the machine {#requirements}

The `kind` binary and a container runtime (Docker or Podman). The cockpit installs neither and does not
manage them — if one is missing, `kind_create` says which one instead of guessing. Just installed it? Retry
the tool; there is no need to restart the cockpit.

The first cluster on a machine pulls a node image of roughly 1.3 GB, so it can take several minutes. After
that, creating one is usually under a minute.

## Every create and every delete asks you {#consent}

Both show the literal `kind` command that will run, and neither approval is ever remembered — approving a
create does not approve the later delete, and each new call asks again.

## How a cluster gets torn down {#teardown}

Three separate rules, all of which skip a pinned cluster:

- **Its owning session closes.** The session that called `kind_create` owns the cluster; when it is gone at
  the next cockpit start, the cluster is removed.
- **The cockpit exits.** Everything not pinned goes.
- **Its lifetime runs out.** The backstop for a cluster whose owner is still around but forgot about it.
  Four hours by default; change it under *Maximum lifetime (hours)* in these settings.

## Pinning {#pinning}

The checkbox on a cluster in these settings keeps it through all three rules. It is operator-only: no MCP
tool can set it, so an agent cannot make its own cluster permanent. Unpin it and the ordinary rules apply
again from that moment.

## Clusters this plugin did not make {#foreign-clusters}

`kind_list` and `kind_delete` only ever see this plugin's own registry. A cluster you created yourself with
`kind create cluster` on the command line is never listed and never touched — remove that one the same way
you made it.

## Reaching a cluster from the Kubernetes plugin {#kubernetes-plugin}

`kind_create` returns the kubeconfig path and the context name it wrote. To use the cluster through the
Kubernetes plugin's tools, add a cluster row there pointing at that path and context.
