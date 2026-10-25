# AC-1673 Assistant Choice Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the operator choose the first-start assistant provider instead of assigning Claude automatically.

**Architecture:** Step 3 persists the installed provider plugins and their provider identities, but no assistant choice. Step 4 opens the existing Assistant Profile editor with only those providers plus Ollama and LM Studio; a provider whose UI has not loaded can be deferred, and the existing coordinator creates that profile after plugin initialization.

**Tech Stack:** C#, Avalonia, xUnit journeys, existing profile editor and plugin registries.

**Spec:** YouTrack AC-1673 and its 2026-10-25 follow-up comment.

## Global Constraints

- No provider is preselected unless step 3 yields exactly one provider.
- Do not duplicate provider-specific profile fields.
- Add no test method; extend `FirstStartJourney` and keep test-count ceilings unchanged.
- Run only the requested filtered journey, lint, and Release build with `-warnaserror` through `start_run`.

## Review Focus

- A detected but untouched Claude suggestion must not become an assistant option.
- Gemini/OpenAI plus Codex must produce those provider choices plus Ollama and LM Studio, with no default.
- A deferred provider must survive wizard-state cleanup and be materialized only after plugin loading.
- A loaded provider must keep using its registered profile fields.
- Local providers must remain directly configurable through the same editor.

---

### Task 1: Pin the first-start behavior

**Files:**
- Modify: `tests/Cockpit.Journeys/FirstStartJourney.cs`

**Interfaces:**
- Consumes: existing first-start provider installation and resume flow.
- Produces: a regression proving the step-4 provider set and absence of a default.

- [ ] Extend the existing journey with untouched found Claude plus selected Gemini and Codex.
- [ ] Run the filtered journey and confirm it fails on the current hardcoded Claude behavior.

### Task 2: Carry provider identities and collect the operator choice

**Files:**
- Modify: `src/Cockpit.Core/Plugins/PluginManifest.cs`
- Modify: provider `plugin.json` manifests used by first start.
- Modify: first-run resume domain/configuration records and stores.
- Modify: provider/assistant step view models and views.
- Modify: assistant-profile dialog service/view model/view.
- Modify: `src/Cockpit.App/Services/DeferredAssistantProfileCoordinator.cs`

**Interfaces:**
- Consumes: plugin-owned provider metadata and the existing `AssistantProfileDialogViewModel`/`EditableProfileViewModel`.
- Produces: filtered provider options, optional deferred profile intent, and an exact provider/config for the coordinator.

- [ ] Persist provider identities without setting `AssistantProviderId` in step 3.
- [ ] Feed only those identities plus local providers to the existing editor in step 4.
- [ ] Add the editor's “Set this up later” path for an unloaded plugin and remember that exact choice.
- [ ] Make the coordinator create the selected profile after the provider registers.
- [ ] Run the filtered journey and confirm green.

### Task 3: Screenshots, counterproof, and gate

**Files:**
- Modify: `src/Cockpit.App/Screenshotter.cs`
- Modify as required: `CHANGELOG.md`, version metadata.

**Interfaces:**
- Consumes: the finished first-start flow.
- Produces: the three requested screenshots and green local evidence.

- [ ] Add/update Screenshotter scenes for step 3, step-4 choice, and Gemini fields; render and inspect them.
- [ ] Commit, mutate the provider-selection branch, run the filtered journey red, revert, and run it green.
- [ ] Run lint and Release build with warnings as errors, review the diff, and verify the branch stat against `origin/epic/AC-1497`.
