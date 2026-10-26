# AC-1722 Uren Dag design

## Intent

Milestone M1 adds the first usable Productivity workspace: the operator records time manually or with one persistent stopwatch, reviews and edits their own entries by day, and sees today's recorded total in the Uren menu badge. It deliberately excludes measured agent time, Week, booking proposals, and overview analytics.

## Constraints

- The host owns all time data through `ICockpitTimeEntries`; the plugin keeps no second store.
- The plugin has a backend assembly and a separate UI assembly. UI access to time data crosses typed plugin-channel handlers so the split also works out of process.
- The manifest uses `minHostVersion` 0.108.0 and `abstractionsVersion` 3 because its menu uses the 3.19 icon/tone additions.
- Only open entries are editable or removable. Offered and booked entries are read-only.
- A stopwatch can start only for today and only when none is running. Its persistence is host behavior already tested by AC-1729.
- The current plugin API exposes neither the active Cockpit project id nor a project catalog. AC-1737 tracks that host API. M1 prefills the last segment of `ActiveSessionWorkingDirectory` and stores it as the temporary project value; no active session means no project.

## Approaches

1. **Backend channel bridge (chosen).** The UI registers surfaces and uses typed request/response contracts; the backend alone calls `host.TimeEntries`. This preserves the established backend/UI boundary and remote-host compatibility.
2. Expose `ICockpitTimeEntries` directly on `ICockpitUiHost`. This is shorter but creates another SDK contract and bypasses the plugin channel boundary.
3. Keep time data in plugin storage. This duplicates authoritative user data and conflicts with AC-1729.

## Structure

- `Cockpit.Plugin.Productivity` registers channel handlers for list, add, update, delete, stopwatch start/update/stop/read, and a combined snapshot.
- Shared contract DTOs live under `Contracts/` and compile into both assemblies.
- `Cockpit.Plugin.Productivity.UI` registers one `workspace.productivity.hours` workspace, the Uren menu button with a textual badge, and the Tijd sidebar section.
- One UI-lifetime controller owns the selected day, current snapshot, running-stopwatch clock, badge calculation, project fallback, and change notifications. Workspace and sidebar render the same controller state.
- The Uren button opens or focuses the workspace. The idle sidebar Start action opens it; while running, the sidebar shows elapsed time, description, and Stop.

## Day behavior

- Quick entry contains description, duration, optional project, Add, and Start stopwatch. Enter on duration adds the entry on the selected day.
- Duration accepts `0:15`, `15m`, `1,5u`, and `1.5u`; zero, negative, malformed, or empty values are rejected with an inline message.
- The selected day's local start is stored with the local offset. Day reads use a half-open local-day range.
- Previous/next, a date picker, and Today change the selected day. Week is visible but disabled with “komt”. Start is disabled away from today or while a stopwatch runs, with the reason visible.
- Open rows expose Edit and Delete. Edit reuses the quick form; Delete asks for confirmation. Offered and booked rows show their status and no mutation controls.
- The textual badge is the sum of today's persisted entries only, formatted like `0u15 vandaag`; a running stopwatch joins the sum only after Stop creates its entry.

## Visual and verification contract

- The code-built Avalonia layout follows mockup v3 screens 01, 02, and 09 without implementing AC-1731's right-hand context column.
- One duration-parser theory lives in the new `Cockpit.Plugin.Productivity.Tests` project.
- One existing-method view journey drives add, stopwatch start/stop, previous-day navigation, and badge updates. It writes `01-uren-dag.png`, `02-uren-dag-stopwatch-lopend.png`, `03-uren-dag-eerder.png`, and `09-menu-zijbalk.png` when `PRODUCTIVITY_RENDER_DIR` is set.
- One redundant existing ViewTests case is consolidated so the project stays at its ceiling; the new plugin test project starts below ten cases.
