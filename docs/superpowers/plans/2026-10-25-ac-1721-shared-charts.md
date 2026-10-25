# AC-1721 Shared Charts Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Provide self-drawn reusable chart controls and migrate UsageTrend to the shared line-chart base.

**Architecture:** Place Avalonia-only controls and their small immutable data contracts in `plugins-dev/_shared/UI`, linked only by UI projects. Keep drawing direct through `DrawingContext`, use existing theme resources, and prove all controls through one headless journey that optionally writes light and dark PNGs.

**Tech Stack:** .NET 10, Avalonia 12, xUnit, Avalonia.Headless.

**Spec:** YouTrack AC-1721; AC-1716 grooming comment, 2026-10-25.

## Global Constraints

- No chart library; controls draw themselves.
- Empty data renders `geen data`, never an empty axis or exception.
- One view journey replaces one existing test in `Cockpit.App.ViewTests`.
- Build plugin projects separately; UsageTrend's version is bumped on migration.
- Screenshots are written only when `CHARTS_RENDER_DIR` is set.

## Review Focus

- Empty and zero-only datasets render their explicit empty state.
- Light and dark themes use resources rather than hard-coded foreground semantics.
- Chart geometry remains bounded for a one-item range and zero available size.
- Shared source is linked only from Avalonia UI projects.
- UsageTrend retains its existing visual output and test contract after the move.

---

### Task 1: Shared drawing controls and view journey

**Files:**
- Create: `plugins-dev/_shared/UI/*Chart*.cs`, `tests/Cockpit.App.ViewTests/SharedChartsJourneyTests.cs`
- Modify: `tests/Cockpit.App.ViewTests/Cockpit.App.ViewTests.csproj`

- [ ] Write the failing journey for fixed stacked, grouped, horizontal, statistic and range-picker data plus empty-data assertions.
- [ ] Run the filtered journey and observe its expected failure before controls exist.
- [ ] Add the smallest direct-drawing controls and link them to the view-test UI project.
- [ ] Run the filtered journey with `CHARTS_RENDER_DIR`; inspect dark and light PNGs.
- [ ] Commit the shared-control journey.

### Task 2: UsageTrend migration

**Files:**
- Move: `plugins-dev/Cockpit.Plugin.UsageTrend/UI/UsageTrendChartControl.cs` to `plugins-dev/_shared/UI/UsageTrendChartControl.cs`
- Modify: `plugins-dev/Cockpit.Plugin.UsageTrend/UI/Cockpit.Plugin.UsageTrend.UI.csproj`, `plugins-dev/Cockpit.Plugin.UsageTrend/plugin.json`

- [ ] Link the moved control in the UI project under its existing namespace-compatible path.
- [ ] Capture before/after UsageTrend renders and verify its 15 focused tests remain green.
- [ ] Bump the plugin version and commit the migration.

### Task 3: Gate and review

- [ ] Run the required warning-as-error builds, lint, view journey and 15 UsageTrend tests via `start_run`.
- [ ] Inspect renders, perform a read-only branch review, and verify the diff contains only AC-1721 files.
- [ ] Push only after every local gate is green; open the PR and notify Zyra with render paths.
