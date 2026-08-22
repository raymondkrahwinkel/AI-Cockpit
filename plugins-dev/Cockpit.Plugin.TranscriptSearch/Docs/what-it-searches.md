---
title: What it searches
order: 10
summary: Why a session can go missing from search results even though you know you wrote in it.
icon: 🔎
---

**Search transcripts** (side menu, or `Ctrl+F`) reads the on-disk transcripts the `claude` CLI writes for
every session and searches the prose in them. It finds a lot, but not everything you might expect — this
page is about the two gaps that are easy to mistake for a bug.

## Only Claude CLI profiles are scanned {#claude-cli-only}

This plugin only looks at the `projects` folder the `claude` CLI keeps transcripts in — the CLI's own
default location, plus the config directory of every profile you have added whose provider is the Claude
CLI. A profile for a different provider keeps its history somewhere else, in a format this plugin does not
read, so sessions run under it never appear here — not because they were missed, but because this plugin
does not look at them at all.

## Only what was said, not what a tool returned {#prose-only}

A transcript line can carry plain prose or a list of content blocks (text, thinking, a tool call, a tool
result). Only the `text` blocks are searched. A word that only ever appeared inside a tool's output — the
contents of a file a session read, say, rather than something you or the agent wrote in the conversation —
will not turn up, even though it is sitting right there in the same transcript file.

## What you get back {#results}

With nothing typed, the dialog opens on your most recently modified sessions — a quick way back into
"what was I just doing" without searching for a word first. Typing at least two characters and pressing
Enter (or **Search**) searches every matching transcript's prose, most recently modified session first, up
to 20 matches per file. Each hit's session id can be copied (to resume it with `claude --resume <id>`) or
its transcript file revealed in the OS file explorer — and, opened from the new-session picker instead of
the side menu, a hit can be used directly to resume that conversation in its original working directory.
