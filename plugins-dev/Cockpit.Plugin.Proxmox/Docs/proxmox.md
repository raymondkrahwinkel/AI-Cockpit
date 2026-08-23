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

Fill in the host (e.g. `pve.example.lan`), the port (`8006` unless you changed it), and an API token's
identity (`user@realm!tokenid`) and its secret UUID, kept in the encrypted secrets layer.

Create the token in Proxmox under **Datacenter → Permissions → API Tokens**. Proxmox separates a token's
privileges from its user's by default (`privsep` on) — the token needs its own ACL entry even if the user
it belongs to already has access. **`PVEAuditor` on `/`** is enough for every read this plugin offers
(nodes, VMs, LXC containers, storage, tasks, snapshots); starting, stopping, snapshotting or deleting
something needs the matching write role on the paths you want reachable.

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
