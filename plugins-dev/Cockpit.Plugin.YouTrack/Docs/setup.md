---
title: Setup
order: 10
summary: Adding a YouTrack instance — the permanent token, the base URL, the project short name, and the one permission that fails silently.
icon: 🎫
---

Each YouTrack instance you add in this plugin's settings is three things: a base URL, a permanent token, and
an optional default project short-name. Nothing here needs a CLI or a local install — YouTrack has none, so
this plugin talks to it over HTTP only, per instance.

## The permanent token {#permanent-token}

In YouTrack, open your avatar in the top-right corner and go to **Profile → Account Security**, then press
**New token** under **Permanent Tokens**. Give it a name you will recognize later (this plugin's own name is
fine) and leave the scope on **YouTrack** — that is the scope this plugin's REST calls need. Copy the token
immediately; YouTrack shows it once and does not let you retrieve it again later, only revoke it and issue a
new one.

Paste the token into this plugin's **Permanent token** field. Treat it the same as a password: anyone holding
it can read and change every issue your account can.

## Finding the base URL {#base-url}

For a YouTrack Cloud instance, the base URL is `https://<instance>.youtrack.cloud/api` — the same subdomain
you use to browse issues, with `/api` appended. For a self-hosted instance, it is
`https://<host>/youtrack/api`, following whatever path your installation is served from.

Leaving off `/api` is the single most common way to get this field wrong, and it does not fail at save time:
this plugin accepts whatever string you type. It only shows up later as every request against that instance
failing, because the URL is pointed at the YouTrack web application instead of its REST endpoint.

## Finding the project short name {#project-short-name}

The default project field is optional — it only preselects a project in the issues dialog's filter when this
instance is picked, and an empty value falls back to "All". When you do set it, use the project's *short
name*, not its full name: the prefix that appears in issue ids, such as the `WEB` in `WEB-14`. It is shown on
the project's own settings page in YouTrack, under **Project name and description**.

## A token without admin access hides projects, not itself {#missing-projects}

This plugin fetches the full list of projects on an instance through YouTrack's admin API, which needs your
token's account to have project-admin read access. A token created without that access does not make this
plugin fail, error, or say anything at all — the request comes back refused, and this plugin quietly falls
back to only the projects that already turned up in the issues it has fetched so far.

In practice that means: right after adding the instance, the project filter in the issues dialog looks
thin — a project nobody has an open issue in yet, or one you have never fetched an issue from, simply is not
in the list, with nothing telling you it was left out. If a project you know exists is missing from the
filter, this is the first thing to check: either grant the token's account project-admin read access, or
accept that the filter will fill in as issues are fetched.

## Picker query {#picker-query}

The **Which issues the session picker shows** field is a YouTrack search query, exactly as YouTrack's own
search bar understands it. The default is `#Unresolved` — showing issues that are already done is offering
work that is over. A board with its own state names works the same way any other query does, for example
`State: {In Progress}`, `#Unresolved -State: Review`, or `#Unresolved Priority: Critical`.

## Branch name pattern {#branch-pattern}

The **Branch name pattern** field decides what branch name this plugin proposes for an issue, independent of
the prompt template below — its placeholders mean something narrower there:

- `{id}` (also accepted as `{ticket}`) — the ticket number, e.g. `WEB-14`.
- `{summary}` — not the full title as in the prompt template: shortened to about 40 characters and made
  git-safe (lowercased, accents stripped, spaces and punctuation collapsed to `-`).

The default is `{id}-{summary}`, giving names like `WEB-14-fix-the-login-redirect`. `feature/{id}` and
`{id}_{summary}` both work the same way.

## Prompt template {#prompt-template}

The **Prompt template** field is what gets dropped into the active session (or the clipboard, with no active
session) when you click an issue. Its placeholders are replaced per issue:

- `{idReadable}` — the ticket number, e.g. `AC-513`; use this one unless you specifically need the internal id.
- `{id}` — the internal YouTrack id, e.g. `2-478`.
- `{summary}` — the issue's title.
- `{url}` — a link to the issue.
- `{project}` — the project's short name, e.g. `AC`, not its full name.
- `{description}` — the full description, or `(no description)` when the issue has none.
