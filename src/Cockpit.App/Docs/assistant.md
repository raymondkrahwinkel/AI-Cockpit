---
title: The assistant
category: system
order: 40
summary: The agent that runs the cockpit itself — turning it on, talking to it, what it may do, and what it remembers.
icon: 🎙️
---

Every other agent in the cockpit works on your code. The assistant works on the *cockpit*: it can tell you
what is running, start and stop sessions, move work between desks, and answer questions about the thing you
are looking at. It is the one agent you can talk to out loud without giving it a keyboard.

It is off until you turn it on. Off means genuinely off — no instance, no model held in memory, no session
costing anything, no chip in the sidebar, and the assistant hotkey does nothing while saying why.

## Turning it on {#turning-it-on}

Options → **Assistant**, first switch. What appears when you do: a chip in the sidebar showing whether it is
listening, and a chat window you can pop out.

The assistant is not the same feature as [dictation](help:settings#voice), and neither needs the other.
Dictation types what you say into whatever has focus. The assistant is somebody to talk to. They have
separate hotkeys for exactly that reason.

## The profile it runs on {#profile}

The assistant runs on a [profile](help:core-concepts#profile) of its own, in a slot that always exists and
cannot be deleted. Its provider *can* be changed — Claude, Codex, a local model — which is the one thing an
ordinary profile is not allowed to do; behind the scenes switching mints a new profile record and repoints
the slot at it, so no profile ever ends up holding credentials for a backend it no longer talks to.

The slot deliberately sits outside the profile list. That is why the assistant never turns up in *+ New
session*, is never offered as a delegation target, and cannot be deleted by tidying the profile list.

If the slot is empty — a fresh install, or a provider switch that failed — Options says so and says why,
rather than presenting an assistant that quietly answers nothing.

## Talking to it {#talking}

Hold the assistant's push-to-talk key (F10 by default, rebindable in
[Shortcuts](help:settings#shortcuts)) and speak. Release, and it answers. You can also just type in the chat
window — the microphone is a convenience, not the interface.

The chip in the sidebar carries the listening mode, and is where you change it:

- **Push to talk** — the default. The microphone is closed and opens only while the key is held. Nothing is
  heard that was not deliberately said to the assistant.
- **Always on** — the microphone stays open and everything said goes to the assistant: an aside to a
  colleague, a phone call, thinking out loud. An honest state rather than a broken one, but it costs per
  utterance, so switching it on says so once. Once, not every time — a warning that returns every time is
  one that gets clicked away unread.
- **Wake word** — visible, and not set up. It is shown rather than hidden so it is clear the possibility
  exists and why it is not available yet.

## Its window {#window}

The chat window is a pop-out onto the assistant's own standing conversation, not a pane in the grid. It
floats above the cockpit by default, can be docked to the right-hand edge by dragging it there, and resizes
like any other cockpit window.

**Closing it does not end the conversation.** The assistant keeps running and keeps its transcript; the
window is a peephole. Its header carries the mode toggle, whether replies are read aloud, and a history menu
holding the log of what the assistant has started and an export of the conversation as a text file.

Replies are rendered at a reading level you set in Options — the same choice a session's own View dropdown
offers — because "tell me what happened" and "show me every tool call" are the same conversation seen at two
depths.

## What it can actually do {#what-it-can-do}

The assistant reaches the cockpit through [MCP servers](help:core-concepts#mcp-server) of its own that no
other session can mount. They come in two halves, on purpose:

**Reading** — what sessions exist and on which [desks](help:workspaces#desks), what they are doing, which
profiles and projects are configured, what a transcript says. Nothing on this half changes anything.

**Acting** — starting a session on a desk with a profile, a project and a first instruction; closing one;
renaming a session or a desk; making an empty desk or taking one away; leaving a message in a running
agent's inbox. This is a separate server from the reading half so that handing out one never quietly hands
out the other.

Both halves answer only to the assistant's own pane, checked against the identity the host stamps on the
request rather than anything a caller can put in an argument. A session that names the assistant's server
gets nothing.

## Consent, and the one switch that widens it {#consent}

Actions that touch your machine normally show a consent card first. For the assistant that friction is
different in kind — it is talking to you at the time — so it has its own bypass in
[Security](help:settings#security): skip every card for the assistant, or, with that off, choose source by
source, with the everyday and the dangerous half as two separate switches rather than one dropdown that puts
"everything" a mouse-movement away from "the harmless things".

Two things hold regardless of how it is set. Every skipped card is still written to the consent trail, so
what was done without asking is readable afterwards. And **the assistant cannot widen its own permissions**:
no tool anywhere writes these settings, so it cannot be talked into granting itself anything. A spoken "yes"
answers the agent's own permission prompt, one layer above, and never reaches here.

The bypass applies only while the assistant is the caller. It never loosens anything an ordinary session
asks, and the chat window says on its face when it is on.

## What it remembers {#memory}

Three files next to the cockpit's own settings, and no database:

- **Its memory** — what you told it to keep, as plain markdown. Plain on purpose: there is no editor for it,
  so opening the file *is* the way to prune it.
- **Its current state** — where it left the conversation before restarting itself.
- **Its transcript** — what you last saw in the window, so the window can be reopened onto the same
  conversation.

The memory can be exported and imported on its own from [Backup](help:settings#backup), separately from the
full archive — it is the one part of the cockpit you might want to carry to another machine without carrying
your credentials with it.

## Where it goes wrong {#troubleshooting}

- **The hotkey does nothing.** The assistant is off, or another program has taken the key. Options →
  Shortcuts lists every key the cockpit captures.
- **It hears things you did not mean for it.** That is Always-on doing exactly what it says. Switch the chip
  back to push-to-talk.
- **It answers in text but never speaks.** Replies-aloud is its own switch, in Options and in the window's
  header — deliberately separate from the assistant itself, so it can be used silently in a shared room.
- **It says it cannot do something.** Check the profile slot is filled and the assistant's own servers are
  mounted; a refusal it explains is not the same as a failure.
