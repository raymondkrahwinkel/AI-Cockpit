# AC-1911 local-model confinement implementation plan

**Spec:** YouTrack AC-1911, grooming comment `Comments-4-8805.0-0`.

**Goal:** LM Studio and Ollama sessions can read and edit their worktree through `cockpit-coding`, every Cockpit-managed worktree launch is confined to that worktree, and `run_elevated` is no longer part of the always-mounted build-agent endpoint.

**Constraints:** Three existing layer-2 tests only; no full test suite; Windows and Linux targeted verification; no context warning unless it is a profile-only assertion in the third test.

## Task 1: lock the three contracts red

- Extend the existing local-model turn test so a saved MCP selection that omits `cockpit-coding` still yields its tool natively above the search threshold and keeps the worktree root in the system prompt.
- Extend the existing isolated-launch test so the driver receives `cockpit.confine-file-tools=true` and the admitted worktree as its working directory.
- Extend the existing confinement test so `cockpit-session` and `cockpit-coding` survive while `cockpit-elevated` does not.
- Run only these three methods and record the expected failures.

## Task 2: implement the bounded behavior

- Force `cockpit-coding` into local-provider selection and preload its tools.
- Add the confinement launch option after worktree admission when `WorktreeBranch` is present.
- Split `run_elevated` into an internal, explicitly selectable `cockpit-elevated` endpoint; keep `cockpit-session` as the safe status/run endpoint and permit it through confinement.
- Re-run the three methods after each minimal change.

## Task 3: verify and prove the counterfactuals

- Build `Cockpit.slnx --configuration Release -warnaserror`.
- Run the targeted methods on Windows and in one `mcr.microsoft.com/dotnet/sdk:10.0` Linux container with the nightly command shape.
- Commit green work, then make and commit one mutation for missing local coding and one for missing worktree confinement; demonstrate each targeted test fails; revert each mutation with `git revert`; return to green.
- Rebase on `origin/main`, inspect `git diff origin/main...HEAD --stat`, push, open the PR, and notify `cockpit-assistant`. Do not merge or run real models before approval.

