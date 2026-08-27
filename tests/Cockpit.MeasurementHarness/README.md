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

**Measured on 2026-08-27, real window, sessions 2 to 6:** the control fires, the frame clock gives 60 to 77
frames per sweep point, and every point reports **one** layout round and **zero** layout loops.

**So the sweep does not yet reproduce AC-1178's threshold** (nothing at 2–4, 153 rounds and 2 loops from 5
on). The setup is reproduced; the amplifier is not. AC-1178's driver is `rail0..3=19 | rail4=325` — the
panel writing `MiniatureFocusChildBox` from inside its own arrange — and that only happens for real
miniature children, where this scenario uses plain borders. **That is the next piece of work on this
scenario, and until it is done this sweep proves the harness, not the threshold.**

**Not here yet:** the other six setups from the requirements' acceptance list (`ac1104`, `ac1120`, `ac1125`,
`ac1169`, `ac1184-heavy`, `ac1119-cpu`), and therefore `C:\temp\cockpit-debug-app` cannot be deleted yet.
