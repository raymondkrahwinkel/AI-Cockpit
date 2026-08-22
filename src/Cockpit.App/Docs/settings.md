---
title: Options and settings
category: system
order: 10
summary: What each page of Options decides, where the answers are stored, and which settings are not there.
icon: ⚙️
---

Options is one window with a list of pages down the left. This page is a tour of that list: what each page
is for, and — more usefully — which of your questions it is *not* the answer to, because a good half of what
looks like a setting lives on a profile, a project, or the session's own header instead.

## Where an answer is stored {#where-settings-live}

Everything in Options is written to a single `cockpit.json`, in the cockpit's own state folder:
`%APPDATA%\Cockpit` on Windows, `~/.config/Cockpit` elsewhere. Beside it live the things that are too big to
be settings — installed plugins, cloned repositories, [worktrees](help:worktrees#where-they-live), project
logos, the assistant's memory, the logs.

Two consequences worth knowing. Everything the cockpit remembers about you is in one directory, so
[backing it up](help:settings#backup) is copying one thing. And those files hold credentials, so they are
written owner-readable only — and can be [encrypted](help:settings#security) behind a password on top of
that.

A development build keeps its own state folder. Running one next to a real cockpit never touches the real
one's profiles, plugins or worktrees.

## Finding a setting without knowing its page {#searching}

The box at the top of Options filters every page at once and puts a count beside each page in the list, so
you can see where the matches are before you go there. Search for what the setting *does* — "worktree",
"push to talk", "encrypt" — rather than what it might be called.

## Sessions {#sessions}

How sessions behave, and where the folders they work in are created.

What lives here is the behaviour that is the same for every session: whether typing `exit` closes a pane,
whether messages queued during a turn are combined into one, when you are warned that a session is running
out of room, and whether other sessions may delegate work through the orchestrator.

Two paths also live here: the root under which isolated sessions get their
[worktrees](help:worktrees#where-they-live), and the root that repositories cloned from a URL land in.
Changing either affects what is made *next* — nothing already on disk moves, so you can point it at a faster
disk without stranding anything.

What is not here: how one session behaves. Its model, effort, permission mode and tools are on the session's
own header, because they are decisions about that conversation.

## Profiles {#profiles}

The [profiles](help:core-concepts#profile) sessions start under — the one page in Options that is a list you
add to rather than a set of switches.

Each profile is edited in place: its provider and credentials, the working directory and standing
instructions it starts sessions with, the defaults *+ New session* pre-fills, which MCP servers it
pre-ticks, environment variables it injects, what it accepts when another session delegates work to it, and
how much memory a session under it may hold before it is cut off.

Which fields are shown depends on the provider — a profile for a backend that has no terminal route is not
offered a choice of route, rather than being offered one it would ignore.

## Appearance {#appearance}

How sessions lay out on screen, and what the transcript and the usage pill show.

The layout choices here — one session at a time, stacked vertically, focus plus rail, or the free grid — are
the cockpit-wide default. Any sessions [workspace](help:workspaces#layout) can arrange itself differently
from its own ⚙, and it then stops following this page. That is the point: a workspace you keep two sessions
side by side in should not have to agree with the one you keep eight in.

## Terminal {#terminal}

The shell a terminal pane opens, and the font it renders in. It applies to plain terminal panes and to
sessions that run the provider's own terminal interface, since both are the same terminal underneath.

## Notifications {#notifications}

When the cockpit tells you a session needs you: a local notification, and — for when you are away from the
machine — a Discord webhook. You choose the events (a session finishing, one falling quiet, a CI failure)
and how long "quiet" has to last before it counts.

## Shortcuts {#shortcuts}

Every key the cockpit captures globally: the screenshot hotkey, push-to-talk for dictation, push-to-talk for
the [assistant](help:assistant#talking), and the full list of in-app keyboard shortcuts.

Global hotkeys are captured by the app whether or not it has focus, so this page exists as much to see what
is taken as to change it — if a key stopped working in another program, this is the list to check.

## Voice {#voice}

Push-to-talk dictation: the microphone, the key, the language, and which transcription model runs it. There
is a first-use calibration so the level is measured on your microphone rather than assumed, and a setting
for what the microphone does while the assistant is speaking.

Dictation and the assistant are deliberately separate. Dictation types into whatever has focus; the
assistant is somebody to talk to. You can use either without the other.

## Assistant {#assistant}

Whether the [assistant](help:assistant#turning-it-on) exists at all, which profile it runs on, whether it
speaks its replies, and how it sounds. The assistant has a page of its own in this knowledge base — it is a
feature rather than a set of switches.

## Security {#security}

Four things that do not otherwise sit together, but all answer "who can get at this".

**Encryption** puts a password in front of the stored credentials, so `cockpit.json` is not readable by
anything that can read your home directory. **Screen lock** locks the cockpit itself after a while.
**Terminal access** decides whether an agent may read and drive terminals through the cockpit's own MCP
server. And the **assistant's consent bypass** decides which consent cards the assistant may skip — the one
setting in the app that widens what an agent may do without asking, which is why it lives here and why the
everyday and the dangerous halves are two separate switches.

## MCP servers {#mcp-servers}

The shared registry of [MCP servers](help:core-concepts#mcp-server): which ones exist, what they are, who
may use them, and how they authenticate.

A server here is a definition, not a connection. It says how to reach the server — a command to run, or a
URL with an API key or an OAuth sign-in — and which kinds of session it is offered to. Which servers a
particular session actually mounts is chosen when that session starts, from this list.

Servers behind OAuth show their sign-in state here, and say so when a token needs renewing. A session
started while a server is unauthenticated gets a server that answers nothing, so this is worth a glance when
tools stop working for no visible reason.

## Nodes {#nodes}

Pairing this cockpit with another one, so the two can share MCP access and so sessions can run on the other
machine. Both halves are here: whether this cockpit accepts connections, and pairing with one that does.

Pairing is deliberately a two-sided act with a code and a pinned certificate. A node is another computer
running your work.

## Backup {#backup}

One archive holding what the cockpit knows: settings, profiles and credentials, and installed plugins. And,
separately, the [assistant's memory](help:assistant#memory) on its own — because that one is a document you
may want to carry somewhere without carrying your credentials with it.

## Updates {#updates}

Whether the cockpit checks for a newer build on start-up, whether it considers nightly builds, and which
build you are on right now. An update is applied on the next start rather than underneath a running session.

## Debug {#debug}

The page you are asked to open when something is being diagnosed: redraw controls, diagnostic snapshots, the
log, the render backend on macOS, and a system diagnostics report to attach to a bug.

Nothing here changes what the cockpit does with your work. It is safe to look at, and safe to leave alone.
