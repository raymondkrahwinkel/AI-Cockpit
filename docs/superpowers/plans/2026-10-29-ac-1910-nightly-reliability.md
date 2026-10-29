# AC-1910 Nightly Reliability Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the nightly locally reproducible on Linux and remove the remaining timing-dependent test outcomes.

**Architecture:** `scripts/local-gate.sh` will expose one Docker-backed test-project path that mirrors the nightly's .NET 10 build and test commands. Flaky tests will control their asynchronous boundary directly instead of waiting for time or probing unrelated sockets. The workflow summary will distinguish a skipped test step from a missing result after a step that did run.

**Tech Stack:** Bash, Docker, .NET 10, xUnit, GitHub Actions YAML, inline Python.

**Spec:** YouTrack AC-1910 (`https://raymondkrahwinkel.myjetbrains.com/youtrack/issue/AC-1910`).

## Global Constraints

- Do not edit `LogPidPositiveControlTests` or `FileLoggerProvider` and its tests.
- Do not add test methods or raise a test-count ceiling.
- Test bodies contain no loop, `if`, or `switch`; theories do not branch assertions by parameter.
- Each repaired flaky test must pass 20 consecutive filtered runs on Windows and in the Linux container.
- Run each touched test project once in full in the Linux container; run `Cockpit.App.ViewTests` last with a 1800-second timeout.
- Commit before counterproof mutations and revert them with `git revert`.

## Review Focus

- A project path outside the repository must not become an arbitrary Docker command or mount target.
- Plugin test projects must receive the same build preparation as the nightly's `PluginTests.slnx` step.
- A test step that ran successfully but emitted no TRX must still say `no test result`.
- A test step skipped after an earlier failure must say `overgeslagen na eerdere fout`.
- Render samples must represent completed render ticks, not wall-clock gaps between dispatcher work.

---

### Task 1: Docker Linux port and operator instructions

**Files:**
- Modify: `scripts/local-gate.sh`
- Create: `AGENTS.md`

- [ ] Add `--linux <test-project.csproj>` validation and run the nightly's .NET 10 build/test sequence in `mcr.microsoft.com/dotnet/sdk:10.0` with `docker run --rm`.
- [ ] Keep the existing no-argument local gate unchanged.
- [ ] Document the mandatory file/IO, process, path, logging, platform-code, and red-nightly cases in `AGENTS.md`.
- [ ] Verify shell syntax, invalid arguments, and one filtered Linux test invocation.

### Task 2: Deterministic flaky-test boundaries

**Files:**
- Modify: `tests/Cockpit.Infrastructure.Tests/Mcp/NodeDiscoveryTests.cs`
- Modify: `tests/Cockpit.App.ViewTests/TranscriptStreamFlickerTests.cs`
- Modify: `plugins-dev/Cockpit.Plugin.Depot/DepotSharedProjectSource.cs`
- Modify: `plugins-dev/Cockpit.Plugin.Depot.Tests/DepotSharedProjectSourceTests.cs`

- [ ] Replace the node test's released ephemeral-port probe with the responder's own unbound-state assertion.
- [ ] Replace transcript wall-clock pumping with a fixed dispatcher/render-tick pump and sample only settled frames.
- [ ] Add an internal quiet-period function seam to `DepotSharedProjectSource` and drive the burst boundary with a controlled task in its existing test.
- [ ] Run every named flaky 20 times by exact filter on Windows and through `--linux`.

### Task 3: Accurate nightly summary

**Files:**
- Modify: `.github/workflows/nightly.yml`

- [ ] Give each test step an id and expose its outcome to the summary step.
- [ ] Map missing projects to their owning step, using `overgeslagen na eerdere fout` only for a skipped step and retaining `no test result` otherwise.
- [ ] Validate the workflow and exercise the inline summary logic with synthetic skipped and missing cases.

### Task 4: Gates, counterproofs, and handoff

- [ ] Commit the implementation before counterproofs.
- [ ] Run a local mutation for each repaired guarantee, confirm red, and `git revert` each mutation back to green.
- [ ] Run the exact local CI gate, all touched Linux projects, and the final 1800-second full ViewTests container run.
- [ ] Rebase with `git -c commit.cleanup=verbatim rebase origin/main`, rerun required gates, push, and create one PR to `main`.
- [ ] Notify `cockpit-assistant` with the inventory and then the PR; do not merge without explicit approval.
