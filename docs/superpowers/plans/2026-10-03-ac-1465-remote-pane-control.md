# AC-1465 Remote Pane Control Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make a remote SDK pane use the backend's session control, usage declarations, queue, and sign-in conversation without moving tokens or plugin objects over the API.

**Architecture:** `ISessionHandle` exposes its local control where one exists; `RemoteSessionHandle` implements that control over `BackendApiClient`. Session snapshots and the single scoped SSE stream carry only allowlisted state. App composition adapts wire DTOs to the existing App-owned plugin-shaped interfaces.

**Tech Stack:** C#/.NET, ASP.NET Core minimal APIs, System.Text.Json, xUnit.

**Spec:** YouTrack AC-1465 and `AGENTS/Zyra/Notes/2026-10-03_AC-1465-design.md` in Depot project `cockpit`.

## Global Constraints

- Every named-pane command is `operate` and checks `NodeCaller.AllowsSession` through `NodeCallerSessionPolicy`.
- Queue SSE events carry only pane scope metadata, wire id, and prompt text; no images or hidden prompt data.
- Test budget: one new `BackendContractTests` fact for interrupt; scope coverage extends the existing theory.
- App viewmodels must not reference `Cockpit.Infrastructure`; Core must not reference Plugins.Abstractions.
- Commit messages start with `AC-1465`; push every completed task.
- VersionPrefix gets one minor bump over main; changelog entry stays under `[Unreleased]`.

## Review Focus

- A queue event without profile/project metadata must not pass the SSE scope filter.
- Duplicate prompt text must still withdraw by stable wire id.
- Unknown queue ids must return 404 and repeated withdraw/clear must remain safe.
- Public live-state may expose model and permission mode, but never working directory, tools, CLI session id, or tokens.
- Cancelling the remote login UI must stop client polling without exposing or moving the server credential.

---

### Task 1: Scoped Server Commands and Shared Interrupt Contract

**Files:**
- Modify: `src/Cockpit.Core/Abstractions/Sessions/ISessionHandle.cs`
- Modify: `src/Cockpit.Infrastructure/Sessions/SessionHostHandle.cs`
- Modify: `src/Cockpit.App/Services/SessionPanelHandle.cs`
- Modify: `src/Cockpit.Infrastructure/BackendApi/SessionsEndpoints.cs`
- Modify: `tests/Cockpit.Infrastructure.Tests/BackendApi/BackendContractTests.cs`
- Modify: `tests/Cockpit.Infrastructure.Tests/BackendApi/SessionsEndpointsTests.cs`

**Interfaces:**
- Produces: `ISessionHandle.Control`, interrupt/model/permission-mode/queue API commands.
- Consumes: existing `NodeCallerSessionPolicy.IsVisibleAsync` and `ISessionControl` methods.

- [ ] Add the shared interrupt contract fact and scope theory rows; run them and observe the missing-control/routes failure.
- [ ] Add the handle-to-control seam and the four routes with validation, idempotent queue behavior, audit, and scope checks.
- [ ] Run the Backend API tests green, commit, and push.

### Task 2: Scoped Queue Stream and Remote Session Control

**Files:**
- Modify: `src/Cockpit.Core/Sessions/QueuedPrompt.cs`
- Modify: `src/Cockpit.Infrastructure/Events/SessionEventsBridge.cs`
- Modify: `src/Cockpit.Infrastructure/BackendApi/EventsEndpoint.cs`
- Modify: `src/Cockpit.Infrastructure/BackendApi/RemoteSessionHandle.cs`
- Modify: `src/Cockpit.Infrastructure/BackendApi/RemoteBackend.cs`
- Modify: existing Backend API tests only.

**Interfaces:**
- Consumes: Task 1 control seam and queue route.
- Produces: stable queue ids, scoped queue SSE snapshots, and `RemoteSessionHandle : ISessionControl`.

- [ ] Extend existing event/contract assertions for queue payload, scope metadata, model/permission-mode allowlist, and remote control behavior; run red.
- [ ] Implement the smallest queue DTO/event path and remote control; explicitly reject local-only members.
- [ ] Run affected tests green, commit, and push.

### Task 3: Usage Declarations as Wire Data

**Files:**
- Modify: `src/Cockpit.Infrastructure/BackendApi/SessionsEndpoints.cs`
- Modify: `src/Cockpit.Infrastructure/BackendApi/RemoteBackend.cs`
- Create or modify App composition adapter files under `src/Cockpit.App/Composition/`.
- Modify: existing session endpoint/App tests only.

**Interfaces:**
- Consumes: session-list snapshot and `IProviderUsageSignals`.
- Produces: provider-neutral usage DTOs and an App-only conversion to `PluginUsageSignal`.

- [ ] Add assertions to existing tests proving the allowlisted wire shape and App conversion; run red.
- [ ] Add the DTO data and minimal adapter without changing App viewmodel dependencies.
- [ ] Run affected tests green, commit, and push.

### Task 4: Remote Login Conversation

**Files:**
- Modify or add files under `src/Cockpit.Infrastructure/BackendApi/` for the remote flow.
- Create or modify App composition adapter files under `src/Cockpit.App/Composition/`.
- Modify existing sign-in/App tests only.

**Interfaces:**
- Consumes: AC-1357 POST/GET/input routes and `ISessionLoginFlows`.
- Produces: remote `ILoginFlow` steps, input submission, completion, and cancellation.

- [ ] Extend existing tests for start/poll/input/completion and verify no token-shaped field crosses; run red.
- [ ] Implement polling with changed-step suppression and caught cancellation/failure paths.
- [ ] Run affected tests green, commit, and push.

### Task 5: Release Metadata, Counter-Proofs, and Final Gate

**Files:**
- Modify: `Directory.Build.props`
- Modify: `CHANGELOG.md`
- Modify: `scripts/test-count-baseline.json` or the baseline selected by `scripts/check-test-count.py`.

**Interfaces:**
- Consumes: all prior tasks.
- Produces: release metadata and reproducible verification evidence.

- [ ] Bump the minor VersionPrefix, add the Unreleased changelog line, update the test-count baseline, commit, and push.
- [ ] Commit before each counter-proof; prove local-only remote interrupt fails and missing scope check fails, then revert the proof commit/change.
- [ ] Run one Release build with `-warnaserror`, Debug build, journeys, and touched test projects with `--no-build` as specified.
- [ ] Request the whole-branch review and the required Claude scope review; fix Important/Critical findings test-first.
- [ ] Open and report the PR; do not merge.
