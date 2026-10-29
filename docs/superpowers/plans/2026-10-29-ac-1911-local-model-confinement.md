# AC-1911 local-model confinement implementation plan

**Spec:** YouTrack AC-1911, grooming comment `Comments-4-8805.0-0`.

**Goal:** LM Studio and Ollama sessions can read and edit their worktree through `cockpit-coding`, every Cockpit-managed worktree launch is confined to that worktree, and local-provider context is explicit and enforced per profile.

**Constraints:** At most four layer-2 tests; no full test suite; Windows and Linux targeted verification. `run_elevated` remains an internal profile capability, while confined `start_run` may execute arbitrary commands only from a working directory inside the worktree. The process boundary remains AC-1908.

## Task 1: lock the three contracts red

- Extend the existing local-model turn test so a saved MCP selection that omits `cockpit-coding` still yields its tool natively above the search threshold and keeps the worktree root in the system prompt.
- Extend the existing isolated-launch test so the driver receives `cockpit.confine-file-tools=true` and the admitted worktree as its working directory.
- Extend the existing confinement test so `cockpit-session` and `cockpit-coding` survive while `cockpit-elevated` does not.
- In that same security test, prove a confined `start_run` working directory outside the worktree is refused and a hidden `cockpit-elevated` profile selection survives the visible launcher checklist.
- Fold a combined local-context contract into the existing local-model test (the repository test-count ceiling stays unchanged): Ollama sends `options.num_ctx`; LM Studio loads an unloaded model, warns for a smaller loaded model when self-load is off, reloads it when self-load is on, and degrades an absent v1 API to a warning.
- Run only these three methods and record the expected failures.

## Task 2: implement the bounded behavior

- Force `cockpit-coding` into local-provider selection and preload its tools.
- Add the confinement launch option after worktree admission when `WorktreeBranch` is present.
- Split `run_elevated` into an internal, explicitly selectable `cockpit-elevated` endpoint; keep `cockpit-session` as the safe status/run endpoint and permit it through confinement.
- Resolve confined `start_run.workingDirectory` through `CodingTools.TryResolve`; this is a file/work-directory boundary, not an OS sandbox (AC-1908).
- Preserve internal server names selected by the profile when the New-session dialog emits its visible selection.
- Add `Context (tokens)` (default 32768) to local profiles and `Model zelf laden met deze context` to LM Studio profiles.
- Send Ollama `options.num_ctx` on every request; before an LM Studio session, inspect `/api/v1/models` and load/reload/warn according to the profile; use the same value for `ChatTurnLimits.ContextWindowTokens`.
- Re-run the three methods after each minimal change.

## Task 3: verify and prove the counterfactuals

- Build `Cockpit.slnx --configuration Release -warnaserror`.
- Run the targeted methods on Windows and in one `mcr.microsoft.com/dotnet/sdk:10.0` Linux container with the nightly command shape.
- Commit green work, then make and commit mutations for missing local coding, missing worktree confinement, and the missing confined `start_run` directory check; demonstrate each targeted test fails; revert each mutation with `git revert`; return to green.
- Rebase on `origin/main`, inspect `git diff origin/main...HEAD --stat`, push, open the PR, and notify `cockpit-assistant`. Do not merge or run real models before approval.

