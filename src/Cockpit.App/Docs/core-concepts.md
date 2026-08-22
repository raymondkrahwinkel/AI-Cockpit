---
title: Core concepts
category: general
order: 20
summary: Session, project, profile, plugin, MCP server — the five words the rest of the app assumes.
icon: 📖
---

Five words carry most of Cockpit. They are easy to mix up because other tools use some of them for other
things.

## Session {#session}

One running conversation with one agent, in one working directory. A session is the unit everything else
attaches to: it has a provider, a profile, a transcript, and its own place in the grid.

Closing a session's panel does not always end it — a session is a process, and the cockpit keeps it if it
is still working.

## Project {#project}

A place you work, remembered: a directory, and what belongs with it. A project is what lets a new session
start already pointed at the right checkout, with the right memory and the right defaults, instead of you
naming them again each time.

A project is not a git repository, though it usually has one. It is the cockpit's own idea of "the thing I
am working on".

## Profile {#profile}

A named set of credentials and settings a session runs under. Profiles are how one machine keeps work and
personal accounts apart, and how two sessions can talk to the same provider as different people.

A profile belongs to a provider. Switching profiles switches who the agent is, not what it can do.

## Plugin {#plugin}

A separately built package that adds something to the cockpit: a provider, a widget, a workspace, a panel,
a settings page. Plugins are loaded at start-up and can be installed from the plugin store or from a file.

Plugins can be written by anyone, which is why the cockpit asks before one does anything on your behalf and
why documentation from a plugin is never trusted to be more than text.

## MCP server {#mcp-server}

A tool provider a session's agent can call, over the Model Context Protocol. Some run as a process on this
machine, some are reached over HTTP, and some are the cockpit's own.

An MCP server is what gives an agent hands. That is also why one is worth being deliberate about: a server
you add is a set of actions you have agreed the agent may take.
