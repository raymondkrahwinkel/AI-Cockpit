---
title: Setup
order: 10
summary: The two ways to connect, and the token scope that silently limits which issues show up.
icon: 🐙
---

This plugin reaches GitHub one of two ways, and most of its settings only make sense once you know
which one you are using.

## Two ways to connect {#connect}

**Use local GitHub CLI (gh)** — on by default — reads issues across every repository the account
behind your local `gh` login can see, scoped to the **Owner** field below (a user or org, or `@me`
for your own repos and the ones you collaborate on). It needs nothing filled in beyond that: no
token, because `gh` already carries your login.

Turn it off to talk to a **single repository** over GitHub's HTTP API instead — the **Owner** and
**Repository name** fields below, plus an optional **Access token**. This is the mode to use when
`gh` is not installed, or when you want this plugin scoped to exactly one repository regardless of
what else the logged-in account can see.

The two modes do not share settings past the prompt template: switching the checkbox does not carry
your owner value across, because "@me" (the gh-mode default) is not a repository owner in HTTP mode.

## Personal access token scope {#token-scope}

The token is optional in HTTP mode — omit it and the plugin reads whatever GitHub's anonymous rate
limit and the repository's own visibility allow, which is enough for a public repository at low
volume. It becomes required for a private repository, and worth adding anyway once you hit the
anonymous rate limit on a busy one.

Create it at [github.com/settings/tokens](https://github.com/settings/tokens). A **classic** token
needs the `repo` scope for a private repository (the narrower `public_repo` is enough only for
public ones). A **fine-grained** token needs **Issues: Read-only** on the repository it is scoped
to. A token scoped to the wrong repository, or missing this permission, does not fail at save time —
this plugin only calls GitHub when the issue dialog opens, so a bad token shows up there as an empty
list or an authorization error, not as a problem with what you just typed into settings.

gh CLI mode needs no token here at all: `gh` carries its own authentication, set up once via `gh
auth login` outside this plugin entirely.

## The owner and repository fields {#owner-repo}

Both come straight from the repository's URL: `github.com/octocat/hello-world` is owner `octocat`,
repository `hello-world`. Get either one wrong and the failure looks the same as a bad token — an
empty issue list — because GitHub does not distinguish "does not exist" from "you cannot see it"
for a repository you are not authorized against.

## Label for work in progress {#in-progress-label}

A GitHub issue has no built-in status field; teams stand in for one with a label, and GitHub does
not enforce a name for it. Leave this blank — the default — and nothing in this plugin offers to
label an issue, because offering a label your repository does not use would fail the moment it was
clicked. Fill in the exact label text your repository already uses (`in progress`, `status: in
progress`) to make that action available.

## Branch name pattern {#branch-pattern}

Controls the branch name this plugin proposes when you start an issue, using `{number}` (or
`{issue}`) and `{title}` (or `{summary}`) — the title is shortened to roughly 60 characters and made
git-safe (lowercased, accents stripped, punctuation collapsed to `-`) before it lands in the branch
name, unlike the same placeholder in the prompt template below, which is inserted verbatim. The
default `{number}-{title}` produces `42-fix-the-login-redirect`; a pattern that resolves to
something git refuses as a ref name (empty, or made entirely of characters that get stripped) falls
back to the issue number alone rather than failing outright.
