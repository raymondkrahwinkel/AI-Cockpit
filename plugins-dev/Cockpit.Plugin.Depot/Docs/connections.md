---
title: Connections
order: 10
summary: Connecting a Depot instance so a project's memory can live there instead of in a folder, and signing in.
icon: 🗄️
---

A connection is a name and a Depot instance's base URL. Once it's signed in, every project offers that
instance's memory under **"Depot: &lt;name&gt;"** — Depot's own OAuth 2.1 + PKCE sign-in is what authorizes it;
this plugin never asks for or holds a token itself.

## Instance URL {#instance-url}

Paste the instance's base URL, e.g. `https://depot.example.com`. Both a bare base URL and one with a trailing
`/mcp` already on it work — the plugin normalizes either form, so you don't need to know which one your Depot
deployment's docs quoted.

Two connections cannot point at the same normalized URL: adding a second row for an instance you already
connected is refused, naming the row that already holds it, rather than silently registering the same source
twice under two names.

## Signing in {#sign-in}

**Sign in** is enabled as soon as the row has a name and a URL — you do not need to save the settings dialog
first. Clicking it saves this row (and every other row in the list) immediately, through the same route the
dialog's own Save button uses, and then opens Depot's sign-in page in your default browser.

Because the save happens first, the token that comes back is filed under whatever name actually made it to
storage — if two rows collided on the same name, the whole save is refused and reported before either row's
stored state changes, so you always know which name a token ends up under.

## Naming a connection {#naming}

Two rows cannot share a name either, case-insensitively — a collision is refused with the colliding name
named, rather than one row silently overwriting the other's registered source. Renaming a connection later is
safe: the project picker and any registered MCP server follow the rename on the next save, with no restart
needed.
