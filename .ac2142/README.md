# AC-2142 disposable Windows transport probe

This is WIP evidence tooling, not a product change or a production IPC contract. Keep it only after the result is reported to cockpit-assistant and retention is agreed. The PR targets `epic/AC-2140-browser-host`; the epic draft targets main and starts from `release/2026-11-bundel` at `145cd2ccdf803d0a0f2d0c740b8f7bb629c00a69`.

All commands run from this worktree via `cockpit-session start_run`, using `py -X utf8`. Scripts use this session's explicit `C:/ck/487726/ac2142` scratch root because `COCKPIT_SCRATCH` is not forwarded by start_run. TEMP/TMP and state are isolated there. No production executable, production state or global input is used. Input goes through CEF APIs. No new test methods, cases, journeys or dependencies beyond the existing pinned CEF/Avalonia packages.

- `build.py functional`: verify pinned CEF archive, generate instrumented source copies, Release build with `-warnaserror`, then four functional scenarios across current/shared/poll routes. These results are not performance evidence.
- `mutation.py`: requires a committed clean WIP; disable frame-ready, build and detect missing updates, `git reset --hard` to that WIP in finally, rebuild and prove shared updates return. Do not edit the worktree while this runs.
- `prepush.py`: required .NET 10 Linux Browser gate and existing full Windows local gate. The `windows` argument resumes only the latter. No workflows or nightly changes.
- `verify_state_root.py`: rerun only the original read-only StateRootOverrideTests class in a separate process without an inherited COCKPIT_STATE_ROOT; no repository test changes.
- `run.py measure --quiet-confirmed`: only after cockpit-assistant's explicit quiet-machine send_prompt. Does no builds. Runs 36 comparisons: 4 scenarios × 3 routes × 3 repeats, alternating order. Active build executables invalidate the comparison. Record the actual message authorizing the window in Depot before running this.
- `report.py <manifest.json>`: validates complete definitive evidence and writes a compact README beside it. Copy its results to Depot `krahwinkel-it/cockpit`, `Reviews/2026-11-06-AC-2142-prestatiepoort/README.md`; do not upload binaries or large raw traces.

Scenarios are 800×600@1, 1920×1080@1, 960×540@2 (1920×1080 pixels), and four active 800×600@1 tabs. Each full run has 40 typing and 40 scrolling events, pixel IDs correlated to copy frame IDs/QPC timestamps, followed by three seconds of identical page animation. Report p50/p95, offered/processed frames, owned-tree CPU, sampled memory, payload copy bytes and buffer allocations. No custom performance tolerance. Missing parity stops the epic at AC-2142; only Raymond decides about a margin or route change.

Frame observations end at bitmap copy, not physical presentation. RSS sums can double count shared pages. Payload allocations exclude fixed maps, bitmap storage and GC internals. The polling control reconstructs the previous documented throwaway route; its original source was not retained. Security, full plugin lifecycle and Linux GPU behavior are outside this Windows transport proof.
