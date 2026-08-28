# Contributing to AI-Cockpit

Thanks for your interest. AI-Cockpit is maintained by one person, so the bar for contributions is
high and the rules below are not negotiable. Reading them before you open a pull request saves
everyone time.

## Not writing code? Still useful

The high bar below applies to **code in pull requests**. A clear, reproducible bug report or a
well-argued feature idea costs you little and is one of the most helpful things you can send.

## Pull request policy

This project is built in an AI-assisted workflow itself (see the README), so to be clear: PRs are
judged on the result, not on what tool produced them. That cuts both ways.

- **PRs that have not been checked by the author are rejected outright.** If you used an AI
  assistant, you are responsible for the result: it must build with zero warnings, pass the tests,
  stay consistent with the surrounding code, and read like code you would sign your name to.
  "My assistant generated it" is not a review.
- **PRs that exist to force an AI review or "another opinion" are closed without discussion.**
  Project direction rests with the maintainer; drive-by rewrites do not.
- **Stay in scope.** One PR, one topic. No unrequested refactors, renames or "while I was here"
  changes bundled in.

A PR that does not meet the bar is closed, not line-by-line reviewed. Don't take it personally —
it keeps a one-person project alive.

## Before you open a PR

1. **Open an issue first** for anything beyond a trivial fix, so the approach can be agreed before
   you spend time on it.
2. **Match the codebase.** Conventions to hold: C# / .NET 10, MVVM (CommunityToolkit), code and
   comments in English, one top-level type per file, no new third-party dependencies without prior
   agreement in the issue, comments explain *why* rather than restating the code. Plugin-eigen
   functionaliteit hoort in de plugin, niet in `Cockpit.Core`/`Cockpit.Infrastructure` — see
   [`docs/plugins/PLUGIN-SDK.md`](docs/plugins/PLUGIN-SDK.md#the-three-layers). Tests assert with
   xunit's own `Assert`, not a third-party assertion library — FluentAssertions is banned (it turned
   commercially licensed at v8; a `BannedApiAnalyzers` rule makes reintroducing it a build error, not
   a review catch).
3. **Build clean and test.** `dotnet build` with zero warnings, `dotnet test` green. New behaviour
   comes with tests; UI changes should be checked visually (`--screenshot` renders the main window
   headless). A test that needs the UI thread goes in `Cockpit.App.ViewTests`, which starts a headless
   Avalonia application. `Cockpit.Core.Tests` starts none, so `Dispatcher.UIThread` there binds to
   whichever thread reaches it first: the test either passes while proving nothing or hangs the run
   with no summary and no failing test. Naming `Avalonia.Threading` in `Cockpit.Core.Tests` is a build
   error (AC-577). Reaching a dispatcher *indirectly* — calling a `Cockpit.App` type that marshals
   internally — is not, and no analyzer sees it; CI's `--blame-hang` is what catches that one, five
   minutes in, by naming the test that stopped.
4. **Mind the trust boundary.** The cockpit never reads, stores or transmits Claude credentials —
   it only checks that a login exists. Anything that would change that needs an issue first.
5. **Record it in the changelog.** When the work is finished, add a bullet under `## [Unreleased]`
   in [`CHANGELOG.md`](CHANGELOG.md) — see *Changelog* below. A finished item that leaves no trace
   there is not finished.

## Running a second instance

Set `COCKPIT_STATE_ROOT` to an absolute path and that instance keeps *everything* there — configuration,
logs, plugins, worktrees, clones, audit trails and caches. Unset, nothing changes: the state root stays
`%APPDATA%\Cockpit` (`~/.config/Cockpit`), or `Cockpit-Dev` for a debug build. Use it to run a release build
beside the cockpit you actually work in, or to put an instance on a prepared configuration for a repro.

Two rules the code enforces rather than documents. The path must be absolute — a relative one would resolve
against each process's working directory, and the cockpit gives its children their own, so a launch with one
fails immediately instead of isolating to two places while reading as one. And nothing may resolve the
application-data directory except `CockpitBuild`: a test fails the build otherwise, because a state root that
moves for most paths but not all is worse than one that never moved.

The single-instance claim follows the state root too, so two release builds with roots of their own run side by
side and two sharing a root still do not — however differently they spell that root. Leave the variable unset
and the claim is the one every earlier version used, which is what keeps an upgrade from stepping over a
running cockpit. A debug build takes no claim at all.

## Git hooks

After cloning, wire the repo's git hooks once:

```
scripts/install-hooks.sh
```

This points `core.hooksPath` at the tracked [`hooks/`](hooks/) directory (no copying, no symlinks;
re-run it any time). The `commit-msg` hook strips AI-attribution `Co-Authored-By: Claude/Anthropic`
trailers from every commit: assistants are tools here, not co-authors, and the trailer would
otherwise list them as repository contributors.

## Commit style

Bullet-list commit messages, one bullet per changed concern, written in English:

```
- added: <what and why>
- fixed: <what it solves>
- changed: <what and impact>
```

Types: `added` · `changed` · `fixed` · `removed` · `refactored`. No summary line above the bullets.

## Changelog

Every finished work item lands in [`CHANGELOG.md`](CHANGELOG.md) under `## [Unreleased]`, so that from
one release to the next it is clear what actually changed — without reading the git log.

- The commit types above map straight onto the changelog sections: `added:` → **Added**, `changed:` /
  `refactored:` → **Changed**, `fixed:` → **Fixed**, `removed:` → **Removed**. Reuse the wording; keep
  it operator-facing (what changed for the person running the cockpit, not the class that changed).
- **No internal tracker numbers.** A reader on GitHub cannot follow an `AC-…`, so keep them out of the
  changelog and the release notes; link a public GitHub issue only when one exists. A commit message may
  keep a tracker reference for internal traceability — the published changelog stays clean.
- Add to `[Unreleased]` — never write a version heading yourself.
- **Releasing is a tag.** Push a `v*` tag (`git tag v1.2.3 && git push origin v1.2.3`) and the Release
  workflow rolls `[Unreleased]` into a dated `## [1.2.3]` section, commits that back, and uses the same
  text as the GitHub release notes. The tag is the version — the About dialog, the updater and the
  changelog all read it.
