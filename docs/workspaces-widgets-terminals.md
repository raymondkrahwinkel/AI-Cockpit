# Workspaces, widget dashboards & plain terminals

*Design + implementation plan. Raised by Raymond 2026-07-14/15: lift Cockpit from an AI-chat client to a workstation shell — named workspaces along the top, plain terminals beside the AI TTYs, and dashboards of plugin-contributed widgets. Functional HTML mock-up: `Memory/Cockpit/Mockups/cockpit-workspaces-widgets-mockup.html` (AI-OS).*

> **Status (2026-07): shipped.** Workspaces, the tab strip, widget dashboards, the add-widget gallery, dashboard
> settings and plain terminal panes are all built and in the app. This note is kept for the design rationale — the
> "not yet built" and phased-plan wording further down is historical.

## Decision (Raymond)

- **One pane model, typed workspaces.** Everything is a pane in the existing draggable grid; a **workspace** is a named, persisted pane layout switched via a "Workspaces" strip above the grid.
- **Two workspace kinds:**
  - **💬 Sessions** — hosts AI sessions + plain terminals (the work surface). `+ New` → AI session / terminal.
  - **📊 Dashboard** — hosts plugin widgets (monitoring/glanceable). `+ Add widget`.
- The `+` on the strip creates a workspace and asks the kind. The kind drives the `+` affordance, the empty state, and the sidebar list, so there are **no dead controls** and widgets never mix into a session grid.
- **Single-user, English UI** unchanged.

## What already exists to build on (real types)

| Need | Reuse |
| --- | --- |
| Draggable 2D tile grid (resize gutters, free placement) | `src/Cockpit.App/Views/SessionTilePanel.cs` + `Cockpit.Core`'s `StackPaneMath` (`_cells`) |
| A pane's content today | `SessionPanelViewModel` (abstract) → `ClaudeTtyViewModel` (TTY) + the SDK session VM |
| pty + terminal renderer | `Porta.Pty` + the Exclr8 renderer already driving the AI TTY |
| Plugin contribution machinery | `ICockpitHost` default-no-op methods + a registry per point (`ConversationPickerRegistry`, `WorkflowStepRegistry`, …) |
| Per-plugin storage, plugin store, consent, SHA-pin | the existing plugin manager |
| cockpit.json persistence | the settings store |
| Command palette / shortcuts | `AddShortcut`, the palette |

## Already landed on this branch (safe, additive)

The **widget SDK contract**, mirroring the conversation-picker point exactly so it is proven shape:

- `Cockpit.Plugins.Abstractions/Widgets/WidgetRegistration.cs` — `Id`, `Title`, `CreateView(IWidgetContext) → Control`, + `Icon`/`Description`/`DefaultColumnSpan`/`DefaultRowSpan`.
- `Cockpit.Plugins.Abstractions/Widgets/IWidgetContext.cs` — per-instance `InstanceId`, `Storage`, `Sessions` observe surface, `RefreshRequested`.
- `ICockpitHost.AddWidget(...)` + `Widgets` (default no-op / empty → older plugins & test fakes still compile).
- `Cockpit.App/Plugins/WidgetRegistry.cs` wires the app host; `WidgetContributionTests` proves a plugin widget reaches the gallery.

This is the plugin surface Raymond most wanted to see; it changes nothing about today's runtime.

**Since extended (AC-122): a plugin can register its own *workspace type*, not just a widget.** Where a widget
fills one cell of a Dashboard, `ICockpitHost.AddWorkspaceType(...)` + `WorkspaceTypeRegistration` +
`IWorkspaceContext` (in `Cockpit.Plugins.Abstractions/Workspaces/`) let a plugin own a whole workspace body — it
appears in the strip's "+" menu beside Sessions and Dashboard — and even embed a live host session inside it via
`IWorkspaceContext.EmbedSession(...)` (the host owns the session's lifetime). `WorkspaceType` became an
extensible value so plugin types sit beside the two host types; an unknown type whose plugin is absent shows a
placeholder rather than crashing. Full API: [`plugins/PLUGIN-SDK.md`](plugins/PLUGIN-SDK.md#workspace-plugins--a-whole-workspace-surface)
and [`plugins/API-REFERENCE.md`](plugins/API-REFERENCE.md#void-addworkspacetypeworkspacetyperegistration-registration).

Since then, the **model, persistence and switching** landed too — still additive, still invisible until the views arrive:

- `Cockpit.Core/Workspaces/` — `Workspace`, `WorkspacePane`, `WorkspaceSettings`, `WorkspaceType`, `PaneKind`, `WorkspaceTypeRules`, `DashboardLayout`, `GridCell`, `DashboardGridMath`.
- `workspaces` section of `cockpit.json` + `WorkspaceSettingsStore`, with recovery for a config that disagrees with itself.
- `WorkspacesViewModel` — the tab strip's commands, wired into `CockpitViewModel`'s shortcut map.
- `WidgetRegistration.CreateConfigView` + `WidgetContext`/`WidgetInstanceStorage` — the per-widget config block.

**Not yet built: any of the views.** The strip, the widget host tile, the gallery and the dashboard settings dialog do not exist, so none of the above is reachable from the UI yet.

## Decisions taken while building (Raymond's opens, filled with the recommendation)

- **Static grid only; no masonry.** Masonry is a second packing algorithm, not a setting on the first one, and it contradicts the one-layout-engine premise. There is deliberately **no layout `Mode` enum**: with only a grid implemented, a `Masonry` value would be a dead option in the settings dialog. Adding masonry later means adding the mode then.
- **`Rows` is a starting height, not a cap.** A fifth widget in a "2x2" grows a row; columns stay fixed, since that is what carries the 2x2/3x2 shape. A hard cap would leave "Add widget" silently doing nothing once the last cell is taken.
- **"Freely draggable" means free placement with holes** — the existing grid's behaviour, and what `DashboardGridMath`'s first-fit preserves by reusing a hole rather than always appending. (Masonry could not offer this: it auto-packs, so holes cannot exist there. That difference is why the two are not interchangeable modes.)

## Widget distribution

A widget ships **inside a plugin** — there is no separate widget package, and no second installer:

- **A plugin can contribute widgets alongside anything else it already contributes**: `AddWidget` sits on `ICockpitHost` next to `AddSettings`/`AddSideMenuButton`/`AddSessionProvider`. A git plugin contributing a git widget needs nothing new.
- **A "standalone" widget (clock, system monitor) is simply a plugin whose only contribution is `AddWidget`.** Same package, same consent + SHA-256 pin, same ALC. Only the presentation differs.
- **The store already has categories.** `PluginStoreEntry.Category` is an existing, additive field, and the store builds its sidebar from the distinct categories it finds. So a widget-only plugin publishes with **`"category": "Widgets"`** in `index.json` and lands in its own section — a convention, not a code change.

## Phased implementation (the rest)

**F0 — Pane abstraction (the foundational, careful refactor).** Generalise `SessionTilePanel`'s cell content from session-specific to an `IPane` (`AiSessionPane | TerminalPane | WidgetPane`), `AiSessionPane` wrapping today's session VM with no behaviour change.
> ⚠️ **Leermoment 2026-07-13:** reorder must stay pure layout via `_cells` — never `Sessions.Move`, which rebuilt the pane and produced a black, pty-less terminal (proven then via a temporary TTY-LIFECYCLE log). Any grid change here needs the same care and a runtime check on a real display, which is why F0 was **not** done blind overnight — it is the first thing to pair on. AI-only header/permission/resume contributions become pane-kind-gated (they must not attach to terminal/widget panes).

**F1 — Terminal panes.** A `SessionKind.Terminal` (shell) that launches `pwsh`/`bash`/`wsl`/`cmd` in the existing pty + renderer instead of `claude`; shell picker + working dir (reuse the recent/favorites the New-session dialog already has). No AI chrome on the header. Cheapest visible win; reuses the whole pty stack.

**F2 — Typed workspaces.** ✅ *Model, persistence and switching done; the strip's view is not.* The type is an invariant that gates which pane kinds a workspace holds and which `+` affordance/sidebar/empty-state show (`WorkspaceTypeRules`). Switching is bound to **Ctrl+Shift+Left/Right** — the arrow defaults split by axis, matching what they move through on screen: Ctrl+Up/Down steps the session list (a vertical sidebar), Ctrl+Shift+arrow steps the workspace tabs (a horizontal strip). Both stay live over a focused terminal, since that is where you switch from.

**F3 — Widget host (uses the contract above).** Render a `WidgetRegistration.CreateView` in a pane with the standard chrome (title, ↻ refresh → `RefreshRequested`, ⚙ config — shown only when `HasConfig`, resize, ✕); the "Add widget" gallery reads `host.Widgets`; per-instance config persisted via `IWidgetContext.Storage`. *The contract half is done; the views are not.*

**F4 — Bundled reference widgets.** Clock, System Monitor (CPU/RAM/disk), Git Status (reuse the git-status plugin data, follow the active session dir via `Sessions`), Notes — bundled like the existing bundled plugins, to prove the SDK end to end. At least one should carry a `CreateConfigView`, so the config path is proven rather than assumed.

**F5 — Polish.** Config dialogs, refresh cadence, drag widgets between workspaces, empty states, command-palette actions (Ctrl+K), store: widget-type plugins. Masonry, if it is ever wanted, is a decision to take here rather than a setting to add.

## Persistence (cockpit.json) — as built

```json
{ "Workspaces": {
    "ActiveWorkspaceId": "ws1",
    "Workspaces": [
      { "Id": "ws1", "Name": "Sessions", "Type": "Sessions", "Layout": null, "Panes": [
        { "Id": "p1", "Kind": "AiSession", "Column": 0, "Row": 0, "ColumnSpan": 1, "RowSpan": 1, "ProfileId": "personal" },
        { "Id": "p2", "Kind": "Terminal", "Column": 1, "Row": 0, "ColumnSpan": 1, "RowSpan": 1, "Shell": "pwsh", "WorkingDirectory": "~/dev" } ] },
      { "Id": "ws2", "Name": "Dashboard", "Type": "Dashboard", "Layout": { "Columns": 2, "Rows": 2 }, "Panes": [
        { "Id": "p3", "Kind": "Widget", "Column": 0, "Row": 0, "ColumnSpan": 1, "RowSpan": 1, "WidgetId": "system-monitor.usage" } ] }
    ] } }
```

Two things this file deliberately does **not** carry:

- **No widget config.** It lives in the plugin's per-instance storage, keyed by the pane id (`widget:{instanceId}:{key}`). Otherwise the host would have to know the shape of every plugin's config, and `cockpit.json` would grow plugin blobs. The pane id *is* the widget's `InstanceId`, which is what ties the two together.
- **No dashboard grid on a Sessions workspace.** It would be a setting nothing reads, which someone editing the file by hand would reasonably expect to do something.

A config that disagrees with itself is recovered rather than rejected — refusing to load costs the whole cockpit over one bad line. A widget pane inside a Sessions workspace is dropped, an unknown type or kind falls back, an out-of-range grid is clamped before it can divide by zero in the view, a dangling active id resolves to the first workspace, and an empty list yields the default.

## Non-goals / orthogonal

- Headless session layer (#68) and delegation (#67) are orthogonal — `IPane` and `SessionRuntime`/`SessionManager` do not conflict; a terminal could later be a headless target too.
- Multi-provider (#26): an AI pane stays profile-bound; terminals/widgets are provider-agnostic.
