---
title: Core concepts
category: general
order: 20
summary: Session, project, profile, plugin, MCP server — the five words the rest of the app assumes.
icon: 📖
---

Five words carry most of Cockpit. They are easy to mix up because other tools use some of them for other
things, and because four of them meet in the same place: starting a session.

Read them in that order if you are new. A **session** is the thing that runs. A **project** says where it
runs and what it is working on. A **profile** says who it runs as. **Plugins** add what the cockpit can do
at all, and an **MCP server** decides what the agent in the session may reach.

## Session {#session}

One running conversation with one agent, in one working directory. A session is the unit everything else
attaches to: it has a provider, a [profile](help:core-concepts#profile), a transcript, and its own place in
the grid.

### What it is fixed to, and what it is not

Four things are decided when a session starts and stay decided for its lifetime: its profile, its working
directory, its route (the cockpit's own chat UI, or the provider's terminal interface hosted in a pane), and
whether it runs isolated in a [worktree](help:worktrees#what-they-are). None of the four can be changed
underneath a running conversation, because each of them is a different program with different state. Wanting
a different one is wanting a second session, and that is a cheap thing to have.

What *does* move while a session runs: the model, the thinking effort, the permission mode, and — where the
provider supports it — which tools are approved without asking. Those live in the session's own header, not
in Options, because they are decisions about this conversation rather than about the cockpit.

### Closing versus ending

Closing a session's panel does not always end it. A session is a process, and the cockpit keeps it if it is
still working; the pane comes back where it was after a restart, and offers to pick up the conversation it
was holding rather than starting a blank one.

That is also why a session can outlive the panel you were watching it in, and why the sidebar is the honest
list of what is running. If you want a session gone, end it — do not close the window and assume.

### Where it goes wrong

- **A session started in the wrong folder.** Everything the agent reads and writes hangs off its working
  directory, and it cannot be moved afterwards. Starting from a [project](help:core-concepts#project) is
  the fix that keeps working: the folder comes with it.
- **A session that quietly holds an old checkout.** An isolated session works in its own worktree, not in
  the folder you are looking at in your editor. The panel says so, and the
  [worktrees page](help:worktrees#the-panel) lists them all.
- **Expecting a fresh session to know what the last one knew.** They share nothing but the files on disk.
  Continuity comes from resuming a conversation, from the project's standing instructions, or from memory a
  [tool](help:core-concepts#mcp-server) fetches — never from having been in the cockpit before.

## Project {#project}

A place you work, remembered: a directory, and what belongs with it. A project is what lets a new session
start already pointed at the right checkout, with the right defaults, instead of you naming them again each
time.

A project is not a git repository, though it usually has one. It is the cockpit's own idea of "the thing I
am working on".

### What a project carries

Beyond the folder, a project can hold a default [profile](help:core-concepts#profile), a behaviour prompt
that every session on it starts with, which [MCP servers](help:core-concepts#mcp-server) are ticked, whether
sessions isolate themselves in a worktree by default, and presentation — a logo, a category to group it
under in the Projects workspace.

The behaviour prompt is worth understanding next to the profile's. A profile's standing instructions say
*who the agent is*; a project's say *how to behave on this work*. Both apply, and the project's is appended
to the profile's rather than replacing it. That split is what lets one identity work on five projects
without five copies of itself.

### What you do with it

You start sessions from it. The Projects workspace is a page of cards, each one Start away, and starting
there is the difference between a session that already knows the folder, the defaults and the standing
instructions and a session you have to configure again. A project can also be shared with a colleague's
cockpit, and a project bound to a shared definition follows what the sharer changes.

### Where it goes wrong

- **A project with no folder is a perfectly good project.** An administrative one — a place to hold
  standing instructions and MCP choices — is a normal thing to have. Nothing forces a directory on you.
- **Deleting a project deletes no files.** It removes the cockpit's memory of them. That is a feature, and
  it is also why "I removed it and my work is gone" is almost never what happened; look for the
  [worktree](help:worktrees#the-panel) first.
- **Renaming.** Projects are found by their own id, not by their name, so renaming one keeps every session
  and every share pointed at it.

## Profile {#profile}

A named identity a session runs under: which provider it talks to, the credentials for it, and the settings
that go with them. Profiles are how one machine keeps work and personal accounts apart, and how two sessions
can talk to the same provider as different people.

### A profile is bound to its provider

Switching profiles switches who the agent is, not what it can do. And a profile cannot change provider: a
different backend means a new profile, so its credentials and configuration can never end up describing
something it no longer talks to. That rule is the reason the profile list tends to grow rather than be
edited — which is fine, because a profile is cheap and a mismatched one is not.

### What a profile decides for a session

| It carries | So that |
| --- | --- |
| Provider and credentials | The session talks to the right backend as the right person. |
| Start defaults — route, model, effort | *+ New session* opens on the right answers instead of the app's. |
| A default working directory | A per-project identity lands in its own folder. |
| Standing instructions | Every session under it starts knowing who it is. |
| Pre-ticked [MCP servers](help:core-concepts#mcp-server) | The checklist opens on this identity's tools, not on all of them. |
| Environment variables | A session's process starts with what that identity needs. |
| A delegation policy | Another session may hand it work — and only the kinds it accepts. |
| A memory ceiling | One runaway session cannot take the machine down with it. |

Every one of those is a default the *+ New session* dialog pre-fills and you can still overrule for one
session. A profile is an opinion, not a cage.

### Where it goes wrong

- **Two profiles, one account.** Nothing stops it, and it is occasionally what you want (different
  defaults, different standing instructions). But rate limits and usage are the account's, not the
  profile's, so two profiles on one account share one ceiling.
- **Expecting a profile to grant permissions.** It does not. What a session may *do* comes from its
  permission mode and its MCP selection; the profile only decides who is asking.
- **The Assistant's profile is not in this list.** The [assistant](help:assistant#profile) runs on a slot of
  its own, deliberately outside the profile list, so it cannot be deleted, delegated to, or picked by
  accident in *+ New session*.

## Plugin {#plugin}

A separately built package that adds something to the cockpit. Plugins are loaded at start-up and can be
installed from a plugin store or from a file.

### What a plugin can add

A session provider, so a new backend appears in the profile editor. A widget for a dashboard. A whole
workspace type. A settings page. Buttons in the strip above the grid. MCP tools of its own. And — since the
knowledge base you are reading — [its own documentation](help:shipping-documentation#shipping), which is why
**Plugins** in the navigation on the left has entries under it at all.

The reverse is worth stating too, because it explains the shape of this app: anything that hangs off one
plugin lives *in* that plugin. If uninstalling it would leave the feature behind, the feature was in the
wrong place.

### Installing one is a decision

Plugins can be written by anyone. So installing one asks first, the exact build you approved is pinned by
its checksum, and each plugin lives in its own folder with its own storage. A plugin declares which host it
needs; one built against a newer cockpit than yours is refused with a reason rather than half-loaded.

Changes to what is installed take effect at start-up. That is not a limitation to work around: a plugin
contributes types the running app has already built its windows out of, and swapping those underneath a live
session is how you get a cockpit that is half of two versions.

### Where it goes wrong

- **A plugin that is installed but contributes nothing you can see.** Check that it loaded — a manifest
  mismatch, a missing runtime, or a plugin left disabled all read the same way from the outside.
- **A `?` that is not there.** A question mark whose page is not installed hides itself rather than opening
  nothing. If a plugin's documentation seems to have vanished, the plugin did.
- **Trusting a page because it looks like ours.** Documentation from a plugin is styled exactly like the
  app's own and carries its author's name for that reason. It is text and nothing else: it cannot run
  anything, and pictures it asks for from the internet are refused rather than fetched.

## MCP server {#mcp-server}

A tool provider a session's agent can call, over the Model Context Protocol. Some run as a process on this
machine, some are reached over HTTP, and some are the cockpit's own.

An MCP server is what gives an agent hands. That is also why one is worth being deliberate about: a server
you add is a set of actions you have agreed the agent may take.

### The registry, and what a session actually gets

Servers are configured once, in Options, and picked per session. The checklist in *+ New session* opens on
what the [profile](help:core-concepts#profile) or [project](help:core-concepts#project) pre-selected, and
what you leave ticked is what that session can reach — for its whole life. A server is also scoped: some are
only useful to a local model that has no built-in tools of its own, and offering those to a provider that
already ships file and shell tools is noise rather than capability.

The cockpit hosts several servers itself — the terminal, the worktrees, the agents on your desk, the
assistant's own. They are why an agent in a session can see its neighbours or claim a branch, and they are
mounted by name like any other.

### Cost and consent

Every tool a server exposes is described to the model on every turn, so an unused server is not free: it
spends the session's context before a word is said. Ticking fewer servers is a real optimisation, not
tidiness.

And a tool that acts on your machine asks. A consent card names the source and what it is about to do, and
the answer is written to a trail whether it was asked or skipped. The one place that can be relaxed
wholesale is the [assistant's](help:assistant#consent) — deliberately its own switch, in its own section,
because it is the one caller that is talking to you at the time.

### Where it goes wrong

- **A server that needs signing in.** An HTTP server behind OAuth expires; Options shows which ones want
  attention, and a session started while a server is unauthenticated simply gets a server that answers
  nothing.
- **Adding a server mid-session.** Sessions mount their servers at start. A server added now reaches the
  *next* session, not the one you are watching.
- **A local process server that is not installed.** A stdio server is a command; if the command is not on
  this machine, the failure is at start-up and is reported there.
