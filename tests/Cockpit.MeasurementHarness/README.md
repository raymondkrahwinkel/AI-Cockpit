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
evidence. Flags: `--scenario`, `--headless`, `--min-sessions`, `--max-sessions`, `--width`, `--height`,
`--settle-ms`, `--repeats`, `--dirty-streak`, `--out`. An unknown flag is refused rather than ignored.
`--dirty-streak=true` (AC-1263) adds the longest stretch a subtree stands still — dirty, set never
shrinking — and what one such sample costs; it is off by default because the tree walk it needs would
move the frame times every other figure in the sweep is about.

Exit codes: `0` measurement, `1` a report with this identity already exists, `2` no SHA, `3` the scenario
produced nothing, `4` the run is a malfunction (see the verdict in its header).

For an app-driven repro (real Cockpit and real session pipelines), run `powershell -NoProfile -ExecutionPolicy Bypass -File tests/Cockpit.MeasurementHarness/run-app-repro.ps1 -Shape growing-tail` from the repository root; it builds and starts only a Debug app whose PID it records, gives it unique `COCKPIT_STATE_ROOT` and `TEMP`/`TMP` folders, validates the host-ready PID/state-root marker before writing the trigger file, then stops only that process. `-Shape new-rows` is the comparison mode. This is deliberately separate from the window-building harness above. The temporary AC-1143 finite-session and full-GC probes are not included: this runner neither uses the former nor permits a blocking collection in its measurement path.

AC-1088's `sdk-read-fallback` shape is fixed at four SDK sessions, each receiving twenty 5-MB orphaned `Read` results. Its `reachableBytes` series is `GC.GetTotalMemory(forceFullCollection: true)` after every round, so it measures retained memory rather than `diag`'s heap-size lines. Run it once normally and once with `-PositiveControl`; the latter additionally holds 20 5-MB values and refuses to pass unless that series rises by at least 100 MB.

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

### What it measures now — 2026-08-30, after AC-1121

**On `main` the threshold no longer reproduces, and AC-1121 is the commit that ends it.** Same harness binary,
same machine, same flags (`--min-sessions=5 --max-sessions=6 --settle-ms=6000 --repeats=3`), positive control
fired in all three; only the production code underneath differs (AC-1104):

| commit | worst frame | layout loops | verdict |
|---|---|---|---|
| `8773496a` — the parent of AC-1121 | 153–161 | 3–9, **6 points of 6** | at the cut-off |
| `e13ef489` — **AC-1121 itself** | 10–14 | **0, 6 of 6** | `MEASUREMENT` |
| `f2ee077f` — `main`, 2026-08-30 | 9–14 | **0, 6 of 6** | `MEASUREMENT` |

The first two are adjacent commits, which is what makes this an attribution rather than a coincidence: AC-1121
took the follow out of `ScrollChanged` and gated it on a tile being visible.

Keep the table below for what the sweep looks like **when the fault is present**; it is the shape a regression
would have to reproduce, and the run against `8773496a` still produces it.

| sessions | worst frame (rounds) | layout loops | passes at the cut-off |
|---|---|---|---|
| 2 | 5–6 | 0 | 0 / 3 |
| 3 | 8–9 | 0 | 0 / 3 |
| 4 | 11 | 0 | 0 / 3 |
| **5** | **159** | **2** | **3 / 3** |
| 6 | 23 or 159 | 0 or 2 | **1 / 3** |

**With the fault present, the threshold at five sessions reproduces deterministically, with the negative
control holding 9 out of 9 below it.** Two caveats belong with that number, not underneath it:

- **Six sessions is flaky here: one pass in three.** AC-1178 reports 153 rounds and 2 loops at six sessions
  on every repetition. This harness keeps each pass's shape in the report as an observation, but judges the
  verdict from the median per session count across all passes. A saturated platform may wobble around its
  plateau; a genuinely declining median still makes the sweep a `MALFUNCTION`.
  The former per-pass check refused the whole sweep when rounds fell from 159 to 23 after adding a session;
  that refusal was the machinery working, and a finding for AC-1178 rather than something to tune away.
   The verdict also has a scale-free per-pass magnitude check: a point below 25% of the other passes'
   median at the same session count is a malfunction. The recorded Windows runs bottom out at 46% (3
   passes) and 50% (10 passes); 23 against 159 is 14%, so the collapse blocks without using a machine unit.
- **`worst frame` reads 159 where Avalonia cuts off at 153.** The counter groups `LayoutUpdated` by frame
  ordinal, so a few rounds either side of the cut-off land in the same bucket. Treat it as "at the cut-off",
  not as an exact figure.

### What a cut-off frame costs

Reaching the cut-off and what reaching it costs are two different measurements, and only the first one was ever
taken. Every sweep point that reaches the cut-off now also reports the price of the frames that got there, from
`FrameMeter.CostOfFramesAtOrAbove`: wall time, share of frames, and bytes allocated on the UI thread per round.
Measured against `8773496a`, six points, positive control fired:

| | |
|---|---|
| one cut-off frame | **800–2842 ms**, against 22–44 ms for the other frames in the same run |
| per sweep point | 2–9 such frames, consecutive, filling 2,2–11,7 s of a 12 s window |
| allocation | **1,89–2,01 MB per round**, against 1,09–1,78 MB per round in the same run's healthy frames |

So a single non-converging frame allocates roughly **300 MB** on the UI thread — 153 rounds at about 2 MB each.
The per-round figure barely moves, which is the point: the amplification is in the **number** of rounds, not in
what a round costs.

**The baseline counts only frames that ran layout.** A frame with no rounds in it is cheap because nothing
happened, and averaging those in flattered the contrast to 13–22 ms — the figures above are the narrower,
honest ones.

**What this does not measure:** the allocation *rate* seen in the field. This harness streams transcript rows
from its own timer, which allocates heavily in healthy frames too, so MB/s here is not comparable to a
production log.

### What a healthy pass's standstill measures — the floor under AC-1263's N

`LayoutLoopGuard` cuts a subtree that stands still for N consecutive dirty samples. The number that decides
whether it ever cuts healthy work is not N itself but **how long a heavy but healthy pass keeps one subtree
dirty with a set that never shrinks**. That is what `--dirty-streak=true` reads, through the production guard
with a bound it can never reach. Measured 2026-08-31 on `1841cb69`, `--min-sessions=2 --max-sessions=6
--settle-ms=6000 --repeats=3`, positive control fired, `VERDICT: MEASUREMENT`:

| sessions | worst streak (samples) | over | dirty samples |
|---|---|---|---|
| 2 | 1 | 0 ms | 217–232 of ~1000 |
| 3 | 1 | 0 ms | 95–152 of ~1000 |
| 4 | 1 | 0 ms | 36–82 of ~830 |
| **5** | **4** | **45 / 91 / 92 ms** | 29–31 of ~700 |
| **6** | **4** | **53 / 83 / 116 ms** | 26–72 of ~720 |

**The comparison has to be made in time, not in samples.** This meter samples at the top of every render tick —
about one per 5–10 ms — while `DiagnosticsBackgroundService` samples once per ten seconds and only while the UI
thread is already flagged unresponsive. So "4 samples" here is 116 ms, not four ten-second intervals.

Against that floor, `DefaultSamplesBeforeCut = 3` requires twenty seconds of standstill between the first
sample and the third: a margin of roughly **170×** over the worst healthy streak measured. It is also the
shape the field already produced — the 31-08 freeze logged three identical dirty sets at 09:08:42, 09:08:52
and 09:09:02, so N=3 would have cut at 09:09:02 rather than letting eleven minutes run.

What this does not establish: that no legitimate pass can stand still for twenty seconds while the UI thread is
already unresponsive. The 170× is a margin, not a proof of impossibility, and `COCKPIT_LAYOUT_CUTOFF_SAMPLES`
is the way out if the field ever produces one.

### What the check costs

Nothing on the healthy path: the guard runs inside the dirty sampling, which does not run at all until the
freeze alarm stands. When it does run, this meter prices one sample — the tree walk plus the guard's
judgement — at **0,5–1,5 ms mean and 26–103 ms worst**, once per ten seconds, on a thread that is already
frozen.

## The second scenario: `--scenario=render-clock`

Parks the render thread from inside `ICustomDrawOperation.Render` — which runs on the render thread, not on
the dispatcher — so the compositor stops committing while the dispatcher carries on. That difference is the
whole point: it is what tells trap 2 (render clock silent, UI thread idle) apart from a busy UI thread.

`--headless=true` is refused immediately with `VERDICT: MALFUNCTION`: headless has no compositor, so this
scenario cannot measure there. It does not wait for a draw operation that cannot run.

**It is built and it does not work yet, and the run says so.** Measured 2026-08-27:

```
POSITIVE CONTROL: parked-render-thread FIRED
VERDICT: MALFUNCTION
  blocked by: the parked clock was detected: the render thread was parked past the app's own threshold
              and no stall was detected
stall detected: False · resume detected: False
dispatcher during the stall: 189 ticks, longest gap 174,9 ms
```

The dispatcher kept ticking (189 ticks), so the negative control holds — the UI thread was genuinely left
alone. What does not hold is the detection itself. **Two candidates, both written into the failure message:**
the commit may complete without waiting for the draw operation, or the parking may not reach it. The most
likely mechanism is that `RenderClockWitness.Probe` starts one probe at a time: if the healthy probe has not
returned when the parked one is requested, the second call is a no-op and `OutstandingFor` ends up measuring
the healthy probe, which then returns.

> **One thing this cost, worth knowing before you touch it.** The first version of the gate counted
> `renderclock-stalled` over the recorder, and it passed — because the *positive control* records into the
> same recorder and satisfied the gate the measurement had just failed. The gate now holds the measurement
> pass's own answer. **That is E2's fault one level up: a check that can be satisfied by something other than
> the thing it is checking.**

## What is here, and what is not

**Here:** the core (`Core/`), the meters (`Meters/`), the session-count sweep (working, threshold
reproduces), and the render-clock scenario (built, not yet detecting, refuses rather than reports).

**Not here:** AC-1169's other two blind spots — **operator input** (wheel, scrollbar thumb, keyboard, touch)
and **window resize** — and the remaining setups from the acceptance list (`ac1104`, `ac1120`, `ac1125`,
`ac1184-heavy`, `ac1119-cpu`). Therefore `C:\temp\cockpit-debug-app` cannot be deleted yet.

For whoever picks those up, the two things that cost this session a run each are in "What the sweep needs"
above, and they generalise: **a stand-in for a real control measures a healthy app that does not exist**, and
**the driver of the layout fault is the follow, not the panel.**
