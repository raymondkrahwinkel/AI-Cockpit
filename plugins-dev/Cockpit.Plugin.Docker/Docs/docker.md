---
title: Settings
order: 10
summary: The daemon endpoint, the exec toggle, and the one setting that silently talks to the wrong Docker.
icon: 🐳
---

This plugin needs no account and no token — it talks straight to a Docker daemon that is already running
on this machine (or reachable from it). Its settings are two things worth understanding before you touch
them: which daemon it connects to, and whether it may run commands inside your containers.

## The daemon endpoint {#endpoint}

Left blank, the plugin uses the local default: a named pipe (`npipe://./pipe/docker_engine`) on Windows, a
Unix socket (`unix:///var/run/docker.sock`) everywhere else. That is correct for the ordinary case of Docker
Desktop or a native daemon running on the same machine the plugin is running on.

Fill it in only when the daemon you want is not the local default — a daemon exposed over TCP
(`tcp://host:2375`), a non-default socket path, or a daemon inside WSL that Docker Desktop is not already
bridging for you. The value goes straight into `Docker.DotNet`'s client configuration; there is no
validation beyond "is this a well-formed URI" until the first call actually reaches the daemon.

## Exec and run — off by default {#allow-exec}

**Allow exec / run into containers** is off until you turn it on. With it off, agents can inspect
containers, read logs, and start/stop/restart what already exists, but cannot run an arbitrary command
inside a container or create a new one from an image. Turning it on hands agents a real command-execution
surface — every exec or run still asks you first, with the literal command shown, but the asking is
per-call, not a one-time consent, so leave this off unless you actually want that capability available.

## When the endpoint quietly points at the wrong daemon {#wrong-daemon}

Nothing in this plugin checks that the endpoint you typed is the daemon you meant. If you run more than one
Docker daemon reachable from this machine — Docker Desktop alongside a daemon inside a WSL distribution, or
a remote daemon left over from an earlier setup — an endpoint that resolves to the *wrong* one does not
fail. It connects, lists containers, and answers every MCP tool call normally; the containers you see and
manage are just not the ones you were thinking of. The tell is a container list that looks empty or
unfamiliar when you know something is running — at that point, check this field before assuming the daemon
itself is down.
