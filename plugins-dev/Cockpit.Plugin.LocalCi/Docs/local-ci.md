---
title: Local CI
order: 10
summary: What "runs locally" actually proves, and where the pull-request gate switch really lives.
icon: 🧪
---

This plugin runs your project's GitHub workflow jobs on this machine's own Docker, driven by the [act](https://github.com/nektos/act)
runtime. It needs both to be in place before any job can run, and this page's settings only change what
happens once they are: the runner image a job executes in, whether sessions are offered the tools at all,
whether every run still asks you first, and how a checkout can be made to hold back its own pull requests.

## Docker and act {#prerequisites}

The settings screen probes both on open and shows one line per runtime — missing, installed but not
answering, or ready for Docker; not on PATH or ready for act — with a concrete reason rather than a single
pass/fail. **Check again** re-probes after you have started Docker Desktop or installed act without closing
the dialog. Nothing below this line means anything until both read ready.

## Runner image {#runner-image}

The image a Linux job runs in. act's own documentation is explicit that its images are not GitHub's runner
images: a job that needs a tool the default (medium) image lacks has nowhere else to say so except here, and
the alternative — shipping everyone a bigger image up front — costs tens of gigabytes nobody asked for.
Leave it blank to use act's default; set it once per machine if a project's jobs need more than that image
carries.

## Offering the tools to sessions {#mcp-tools}

Turning this off removes the `cockpit-local-ci` MCP tools from every session — an agent can no longer start a
run or read a verdict back at all, not even with your approval. It does not touch the **Run CI on this
machine…** action in a session's own header; that stays available either way, because it is you pressing it,
not the agent asking.

## Skipping consent {#skip-consent}

Off by default, and worth leaving off on a fresh install. A session can still ask to run a workflow job with
this off — you are just asked to approve the exact command every single time, which is what keeps "the agent
ran something in a container on my machine" from ever happening silently. Turning it on does not narrow what
can run: it is still whatever the project's workflow says, in the same container with the same access to
this machine's Docker. All it removes is you seeing that command before it runs.

## The pull-request gate is not on this page {#pull-request-gate}

There is a **Hold back pull requests from this checkout until a local run has passed** checkbox, but it does
not live in this settings dialog — it is on the **Local CI** run view itself, opened from **Run CI on this
machine…** in a session's header, and it is set per checkout rather than globally. Look for it there, not
here, if it seems to be missing.

Turning it on for a checkout does not mean "CI passed" in any general sense — it means one specific thing: the
last local run in *this* checkout finished with a pass, and the checkout's current commit is still the exact
commit that run was against. Switch branches, make a new commit, or simply never have run anything here yet,
and the gate reports "did not run" rather than inventing a pass from an old result — that distinction is the
whole reason the gate exists rather than a plain boolean. A held-back pull request is not stuck forever: the
place that tried to open it offers an explicit bypass through the same consent prompt every dangerous action
here goes through, so the decision to go around a failing or missing local run is visible and on the record,
not silent.

## What a local pass proves, and what it does not {#local-vs-ci}

A run here executes in a container built from act's own images, driven by act's own interpretation of the
workflow file — not GitHub's runners, and not GitHub's own Actions engine. A job the plugin cannot make sense
of at all — one that uses a matrix, a non-Linux runner, artifacts handed between jobs, or an action that only
means something inside GitHub's own infrastructure — is refused outright with the concrete reason, never
attempted partially and called done. A green result here is a strong local prediction of what GitHub's check
will say next; it is not a replacement for that check, and nothing in this plugin claims otherwise.
