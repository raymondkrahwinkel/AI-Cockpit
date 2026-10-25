# AC-1718 TTY Operator Input Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Record privacy-safe, 30-second-batched operator input moments for TTY panes.

**Architecture:** `TtyView` alone identifies successful operator-originated PTY writes and applies the 30-second window. `TtyViewModel` and `SessionPanelHandle` forward the content-free `TtyInput` action to the existing `SessionTimelineRecorder` subscription.

**Tech Stack:** C# 14, .NET 10, Avalonia, xUnit

**Spec:** `docs/superpowers/specs/2026-10-25-ac-1718-tty-operator-input-design.md`

## Global Constraints

- Do not record input bytes, key codes, or lengths.
- Do not count `Terminal.Output`, `PromptSink`, terminal-access `RegisterInput`, agent, or assistant input.
- Do not modify `Cockpit.Plugins.Abstractions` or the retention implementation.
- Build with warnings as errors before every push.

## Review Focus

- A failed PTY write must not create an operator action.
- Terminal protocol replies must not create an operator action.
- Pushed text must not create an operator action or leak into JSONL.
- Repeated keystrokes within 30 seconds must create one timeline row.
- `TtyInput` must round-trip through the JSONL reader.

---

### Task 1: TTY input recording

**Files:**
- Modify: `src/Cockpit.Core/Abstractions/Sessions/SessionOperatorAction.cs`
- Modify: `src/Cockpit.App/ViewModels/TtyViewModel.cs`
- Modify: `src/Cockpit.App/Services/SessionPanelHandle.cs`
- Modify: `src/Cockpit.App/Views/TtyView.axaml.cs`
- Modify: `third_party/Exclr8.Terminal/TerminalControl.cs`
- Modify: `tests/Cockpit.App.ViewTests/TtyInjectedTextPasteTests.cs`
- Modify: `scripts/test-count-baseline.txt`

**Interfaces:**
- Consumes: `SessionPanelHandle.OperatorActed` and `SessionTimelineRecorder`'s existing event subscription.
- Produces: `SessionOperatorAction.TtyInput` raised at most once per 30 seconds for successful `Terminal.Input` writes.

- [ ] **Step 1: Write the failing L2 theory**

Convert the existing pushed-input test into a two-case theory. Assert that typed input writes one `Operator:TtyInput` row despite repeated immediate input, pushed input writes none, neither secret appears in JSONL, and the recorded row is readable as `TtyInput`. Remove one redundant paste transport fact and lower the view-test baseline by one.

- [ ] **Step 2: Run the focused theory and confirm RED**

Run: `dotnet test tests/Cockpit.App.ViewTests/Cockpit.App.ViewTests.csproj --filter FullyQualifiedName~TtyInjectedTextPasteTests.<new-theory-name>`

Expected: FAIL because `TtyInput` and the TTY event bridge do not exist.

- [ ] **Step 3: Implement the minimal input path**

Add `TtyInput`, raise it from `TtyViewModel`, forward it from `SessionPanelHandle`, split `Terminal.Input` from `Terminal.Output`, and record only after successful operator PTY writes outside the named 30-second interval.

- [ ] **Step 4: Run the focused theory and confirm GREEN**

Run the same filtered test command. Expected: both theory rows pass.

- [ ] **Step 5: Verify and commit**

Run the requested local gate through `start_run`: warning-as-error build, lint, and the one focused theory. Commit and push only after all checks are green.
