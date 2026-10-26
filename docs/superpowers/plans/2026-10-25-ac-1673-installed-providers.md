# AC-1673 Installed Providers Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make first-start step 4 offer every provider that is already available, including bundled Claude and an approved installed Codex, without requiring a restart.

**Architecture:** Generalize PR #1424's early Cockpit/plugin startup path to ordinary first start, then build first-run profile choices from the loaded provider registry plus any resume metadata. The registry remains the approval boundary: bundled plugins are seeded as approved, while installed but unapproved plugins never register and therefore never appear.

**Tech Stack:** C#, Avalonia, xUnit journey, existing plugin lifecycle and Assistant Profile editor.

**Spec:** YouTrack AC-1673, all three comments, PRs #1419/#1424, and Raymond's 2026-10-25 follow-up.

## Global Constraints

- Branch from `origin/epic/AC-1497`; target the same epic.
- Reuse the existing Assistant Profile editor and PR #1424 deferred-profile path.
- Never bypass plugin consent, enablement, or closure-hash pinning.
- Extend the existing `FirstStartJourney`; add no test method and keep test-count ceilings unchanged.
- Prove the regression by mutation/revert locally; never push a red commit.
- Run build with `-warnaserror`, lint/test-count gates, and the filtered journey through `start_run` before push.
- Render step-4 screenshots headlessly and inspect them; do not touch a running Cockpit.
- Never run `dotnet build-server shutdown`.

## Review Focus

- Fresh first start with no provider plugins still offers exactly Ollama and LM Studio.
- Fresh first start with bundled Claude offers Claude without a restart.
- Bundled Claude plus approved installed Codex offers both without a restart.
- Restart after installing Gemini still offers Claude, Gemini, and OpenAI.
- Installed but unapproved provider plugins stay absent.

---

### Task 1: Load and expose approved providers before step 4

**Files:**
- Modify: `tests/Cockpit.Journeys/FirstStartJourney.cs`
- Modify: `src/Cockpit.App/ViewModels/Onboarding/FirstRunWizardResume.cs`
- Modify: `src/Cockpit.App/ViewModels/Onboarding/AssistantStepViewModel.cs`
- Modify: `src/Cockpit.App/Screenshotter.cs`

**Interfaces:**
- Consumes: `App._StartCockpit`, `InitializePlugins`, `IPluginRegistrations.SessionProviders`, and PR #1424's `AssistantProfileSetup`/resume metadata.
- Produces: a first-run provider list containing built-ins plus loaded approved providers, with resume-only providers retained as a deferred fallback.

- [ ] Extend the existing journey with Claude bundled, Codex approved, and Gemini installed without approval; assert step 4 offers Ollama, LM Studio, Claude, and Codex, excludes Gemini, and required no restart.
- [ ] Run the filtered journey and verify it fails because the normal first-start path has not initialized plugins and the setup list ignores the registry.
- [ ] Let the existing startup condition route an ordinary first start through the same early Cockpit/plugin initialization used by resumed onboarding.
- [ ] Merge registered providers with resume metadata in `_FirstRunSetup`, deduplicated by provider id.
- [ ] Run the filtered journey and verify it passes.
- [ ] Add the smallest screenshot scenes needed for the five review scenarios, render them headlessly, and inspect every image.
- [ ] Commit the green implementation, perform the requested mutation/revert counterproof, and rerun the filtered journey green.
- [ ] Run the complete local CI-equivalent gates before push and record the exact results in the PR.
