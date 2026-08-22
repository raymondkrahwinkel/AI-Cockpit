---
title: Setup
order: 10
summary: The two ways to connect, the token scope that silently limits what shows up, and the "watch" fields.
icon: 🐙
---

Like the GitHub Issues plugin, this one reaches GitHub one of two ways, and most of its settings only make
sense once you know which one you are using.

## Two ways to connect {#connect}

**Use local GitHub CLI (gh)** — on by default — lists open pull requests across every repository the account
behind your local `gh` login can see, scoped to the **Owner** field below (a user or org, or `@me` for your
own repos and the ones you collaborate on). It needs nothing else filled in: no token, because `gh` already
carries your login.

Turn it off to talk to a **single repository** over GitHub's HTTP API instead — the **Repository owner** and
**Repository name** fields, plus an optional **Access token**. Use this mode when `gh` is not installed, or
when you want this plugin scoped to exactly one repository regardless of what else the logged-in account can
see. Switching the checkbox does not carry your owner value across between modes — `@me` is not a repository
owner in HTTP mode.

## The owner and repository fields {#owner-repo}

Both come straight from the repository's URL: `github.com/octocat/hello-world` is owner `octocat`,
repository `hello-world` — the same fields whether you are filling in the gh CLI mode's **Owner**
(who to search) or the HTTP mode's **Repository owner** and **Repository name** (the one repository
to read). Get either one wrong and the failure looks the same as a bad token — an empty pull request
list — because GitHub does not distinguish "does not exist" from "you cannot see it" for a repository
you are not authorized against.

## Personal access token scope {#token-scope}

The token is optional in HTTP mode — omit it and the plugin reads whatever GitHub's anonymous rate limit and
the repository's own visibility allow. It becomes required for a private repository, and worth adding anyway
once you hit the anonymous rate limit on a busy one.

Create it at [github.com/settings/tokens](https://github.com/settings/tokens). A **classic** token needs the
`repo` scope for a private repository (`public_repo` is enough for a public one). A **fine-grained** token
needs **Pull requests: Read-only** on the repository it is scoped to. A token scoped wrong, or missing this
permission, does not fail at save time — this plugin only calls GitHub when the list is fetched, so a bad
token shows up there as an empty list or an authorization error, not as a problem with what you typed into
settings.

gh CLI mode needs no token here at all: `gh` carries its own authentication, set up once via `gh auth login`
outside this plugin entirely.

## Beyond your own pull requests {#watching}

The **watch** fields answer a different question than the rest of the list. Everything else here is "which
pull requests are mine" — authored by you, assigned to you, waiting on your review. **Watch every repository
I'm involved with** widens that to every open pull request in every repository you own, collaborate on, or
reach through an organisation, whoever opened it — gh works out which repositories those are, so there is no
list to keep current, and it needs gh CLI mode. **Watch these repositories as well** is the narrower version
for a specific repository you are *not* otherwise involved with — one `owner` (every repo of that user or
org) or `owner/repo` per line; it is redundant once the box above is ticked.

## Only these repositories {#repo-filter}

Limits the whole list — your own pull requests and anything watched — to specific repositories, one
`owner/repo` per line (or comma-separated). Leave it blank to show pull requests from everywhere the rest of
the settings above already reach; this field only narrows, it never widens past them.
