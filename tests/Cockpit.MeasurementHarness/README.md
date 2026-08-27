# The measurement harness (AC-1131)

One harness, in the repo, instead of throwaway copies under `C:\temp`. On 2026-08-27 there were **sixteen**
of those, **10.415 lines**, including copies of copies whose thirty-line differences nobody could account
for — the same fault this epic is about, in tooling rather than in production code.

The requirements are in `Plans/AC-1131-meetharnas-eisen.md` (Depot). Every one of them comes from a measured
mistake; nothing is here because it seemed like good practice.

## Running it

```
COCKPIT_GIT_SHA=$(git rev-parse HEAD) \
  dotnet run --project tests/Cockpit.MeasurementHarness -- --min-sessions=2 --max-sessions=6 --out=<dir>
```

`COCKPIT_GIT_SHA` is required, not defaulted: a report that cannot name the code it measured is not
evidence. Flags: `--headless`, `--min-sessions`, `--max-sessions`, `--width`, `--height`, `--settle-ms`,
`--out`. An unknown flag is refused rather than ignored.

Exit codes: `0` measurement, `1` a report with this identity already exists, `2` no SHA, `3` the scenario
produced nothing, `4` the run is a malfunction (see the verdict in its header).

## What the harness refuses to do

- **Produce a report without having run its positive control** (E1). Not a convention — `Finish()` throws.
- **Count a phase marker as the detector it names** (E2). Detector events, phase markers and measurements
  are three types; the count only ever sees the first.
- **Take a full blocking collection inside the measurement window** (E3). `GcMeter.ReachableBytes` demands
  the verification phase, and measuring after verification has begun throws.
- **Overwrite an earlier report** (E4). The filename is derived from the full argv; a collision refuses.
- **Report a figure it has no basis for** (E5, and the meters). A series too short to judge says so, a meter
  without a baseline says `n/a` rather than `0`, and a sweep with too few frames is a malfunction.

## The CI boundary

Anything that needs a real window does not run on a headless CI runner. That is a property, not a
shortcoming — but it has to be **marked**, because a suite that reports 100% green while half of it never
ran is the same fault as a positive control that does not fire.

- **In CI:** the decision functions — `Series`, `RunIdentity`, `Recorder`, `PositiveControl`,
  `MeasurementRun`'s phase and verdict rules, `CpuMeter`'s baseline rule. They live in
  `tests/Cockpit.App.ViewTests/MeasurementHarnessTests.cs` and each one names the mistake it prevents.
- **Not in CI:** the scenario. It needs a real compositor, and it is run with the command above.

## What is here, and what is not

**Here:** the core (`Core/`), the meters (`Meters/`), and one scenario — the session-count sweep over the
real `SessionTilePanel` in focus+rail, with a self-invalidating child as its positive control.

### What the sweep needs before it reproduces anything

Two things had to be real before the threshold appeared, and each was worth a run of its own to establish:

1. **The pane has to be shaped like a pane.** A `PaneRoot` container with a real `MiniatureHost` inside,
   bound to the panel's attached boxes exactly as `CockpitView.axaml` does it. With plain borders in that
   place every sweep point reported one layout round and zero loops — a picture of a healthy app.
2. **There has to be a follow.** AC-1178's driver is `_MoveTo`, the scroll-offset write; adding the
   miniature host alone changed nothing. Only with a streaming, virtualised transcript whose stick-to-bottom
   follow writes the offset from `ScrollChanged` did the threshold appear.

### What it measures now — 2026-08-27, real window, `--repeats=3`

| sessions | worst frame (rounds) | layout loops | passes at the cut-off |
|---|---|---|---|
| 2 | 5–6 | 0 | 0 / 3 |
| 3 | 8–9 | 0 | 0 / 3 |
| 4 | 11 | 0 | 0 / 3 |
| **5** | **159** | **2** | **3 / 3** |
| 6 | 23 or 159 | 0 or 2 | **1 / 3** |

**The threshold at five sessions reproduces deterministically, with the negative control holding 9 out of 9
below it.** Two caveats belong with that number, not underneath it:

- **Six sessions is flaky here: one pass in three.** AC-1178 reports 153 rounds and 2 loops at six sessions
  on every repetition. This harness does not reproduce that, and the run says so — the shape test refuses
  the sweep as a whole (`MALFUNCTION`) because rounds drop from 159 to 23 when a session is added.
  **That refusal is the machinery working, not a bug in it**, and the discrepancy is a finding for AC-1178
  rather than something to tune away here.
- **`worst frame` reads 159 where Avalonia cuts off at 153.** The counter groups `LayoutUpdated` by frame
  ordinal, so a few rounds either side of the cut-off land in the same bucket. Treat it as "at the cut-off",
  not as an exact figure.

**Not here yet:** the other six setups from the requirements' acceptance list (`ac1104`, `ac1120`, `ac1125`,
`ac1169`, `ac1184-heavy`, `ac1119-cpu`), and none of AC-1169's three blind spots — operator input, window
resize, a parked render clock. Therefore `C:\temp\cockpit-debug-app` cannot be deleted yet.
