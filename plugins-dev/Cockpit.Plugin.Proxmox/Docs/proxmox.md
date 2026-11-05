---
title: Settings
order: 10
summary: The API token and its permissions, the certificate fingerprint, and the two capabilities off by default.
icon: 🖥️
---

This plugin talks to the Proxmox VE REST API with an API token — never your Proxmox password, never a
browser session ticket. Its settings are three things worth understanding: the token's permissions, the
certificate it connects over, and the two capabilities that reach past ordinary VM/LXC management.

## Connecting {#connecting}

Register each Proxmox VE installation with a unique name. Targets are recorded as PVE so a later release can add another Proxmox product type without changing saved target identities. MCP tools accept an optional `server` name; omitting it is supported only when exactly one server is configured. `list_servers` shows names, hosts and whether each server is ready, never tokens. All server tokens live in the declared `apiTokens` secret key, so settings exports scrub them. Existing single-server settings migrate automatically on first read.

Fill in the host (e.g. `pve.example.lan`), the port (`8006` unless you changed it), and an API token's
identity (`user@realm!tokenid`) and its secret UUID, kept in the encrypted secrets layer.

Create the token in Proxmox under **Datacenter → Permissions → API Tokens**. Proxmox separates a token's
privileges from its user's by default (`privsep` on) — the token needs its own ACL entry even if the user
it belongs to already has access. **`PVEAuditor` on `/`** is enough for every read this plugin offers
(nodes, VMs, LXC containers, storage, tasks, snapshots); starting, stopping, snapshotting or deleting
something needs the matching write role on the paths you want reachable.

## PBS targets {#pbs}

Register a PBS target with kind **PBS** and its normal port **8007**. `list_servers` returns the kind, and a PVE tool rejects a PBS target (and vice versa) before it reaches the API. PBS read tools expose datastores, backup snapshots, recent tasks and verify/prune/sync jobs. The only PBS actions are starting an existing verify job and starting garbage collection; each asks afresh with the literal target. This plugin never deletes or prunes backups and never changes the protected flag.

Give a PBS token **DatastoreAudit** on every `/datastore/<name>` it reads. PBS task history only includes the token's own tasks unless it also has **SysAudit** on `/system/tasks`. Starting a verify job needs **DatastoreVerify** on its datastore; starting garbage collection needs **DatastoreModify**. Do not grant DatastorePrune for this plugin.

Endpoints, permissions and response shapes are verified from the official [Proxmox Backup Server API Viewer](https://pbs.proxmox.com/docs/api-viewer/) and [User Management](https://pbs.proxmox.com/docs/user-management.html), documentation version **4.2.8-1** (2 October 2026): `/admin/datastore`, `/admin/datastore/{store}/snapshots`, `/nodes/localhost/tasks`, `/config/verify`, `/config/prune`, `/config/sync`, `/admin/verify/{id}/run` and `/admin/gc/{store}`.

## The certificate {#certificate}

A certificate from a real CA (say, a reverse proxy in front of Proxmox with a Let's Encrypt certificate)
just works — nothing to trust by hand, and it keeps working through renewals. Proxmox is self-signed by
default though, and this plugin never accepts a self-signed certificate silently: click **Show fingerprint**
to connect and read what the host presents — the same trust-on-first-use step an SSH client takes with a
host key. Verify the SHA-256 fingerprint shown matches your Proxmox host's own (visible on its console, or
via `openssl x509 -noout -fingerprint -sha256 -in /etc/pve/local/pve-ssl.pem` on the host itself) before
clicking **Trust this certificate**. Every connection after that is checked against exactly that fingerprint;
a host whose certificate changes — a renewal, a reinstall, or someone in the middle — fails closed with a
message pointing back here, never a silent reconnect.

## Rollback and delete — off by default {#dangerous-capabilities}

**Roll back a snapshot** restores a VM or LXC container to an earlier point in time, destroying everything
that happened since. **Delete** removes a VM or LXC container outright. Both are off until you turn them on;
turning one on does not skip its confirmation — every rollback or delete still asks you first, with the
literal VM/LXC id and node shown, every time.

## Shutdown vs. stop {#shutdown-vs-stop}

Proxmox treats a graceful shutdown (an ACPI request the guest OS can act on) and a hard stop (immediate,
like pulling the power cord) as different operations, and so does this plugin: `shutdown_vm`/`shutdown_lxc`
and `stop_vm`/`stop_lxc` are separate tools with their own consent text, never merged into one "stop" button
that hides which one you approved.
