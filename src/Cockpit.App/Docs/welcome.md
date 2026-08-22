---
title: Welcome
category: general
order: 10
summary: What this window is, and how to find your way around it.
icon: 👋
---

This is Cockpit's own documentation. It ships with the app, so it works with no connection, and it never
describes a version you do not have: every page travels inside the thing it documents.

## Where the pages come from {#where-pages-come-from}

The four groups on the left belong to the app.

- **General** — what Cockpit is and the words it uses.
- **System** — the parts of the app itself: settings, worktrees, workspaces, the assistant.
- **Extending Cockpit** — how to write a plugin of your own.
- **Plugins** — one entry per installed plugin that ships documentation. What sits under it is that plugin's
  own doing.

A plugin cannot add a group of its own or place itself outside **Plugins**. That is deliberate: a tree
anyone can grow is unreadable by the tenth plugin, and documentation you did not write should not be able
to seat itself among the app's own pages.

## Finding an answer {#finding-an-answer}

Search, at the top of the navigation, covers every page at once — the app's, and every installed plugin's.
A result takes you to the section that answers it rather than to the top of a long page.

## Arriving from somewhere else {#arriving-from-elsewhere}

A `?` next to a setting, or a link in an error message, opens the page about exactly that thing. When you
arrive that way you land mid-article, so a banner at the top says where you came from and offers the way to
the beginning of the page you are standing in.

If a `?` points at something that is no longer there — a plugin you removed, a section that was rewritten —
the window says so plainly. A broken reference is worth seeing; a reference that quietly opens the wrong
thing is not.

## Who wrote a page {#who-wrote-a-page}

A page from a plugin someone else wrote carries that author's name beside its title. It reads exactly like
ours and is styled exactly like ours: the point is not that it is worth less, only that you can see whose
instructions you are about to follow.

Documentation from a plugin is treated as text and nothing else. It cannot run anything, and a picture it
asks for from an address on the internet is refused rather than fetched — opening a page should never be
the moment a stranger's server learns you exist.
