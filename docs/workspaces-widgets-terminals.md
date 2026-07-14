# Workspaces, widget dashboards & plain terminals

*Design + implementation plan. Raised by Raymond 2026-07-14/15: lift Cockpit from an AI-chat client to a workstation shell — named workspaces along the top, plain terminals beside the AI TTYs, and dashboards of plugin-contributed widgets. Functional HTML mock-up: `Memory/Cockpit/Mockups/cockpit-workspaces-widgets-mockup.html` (AI-OS).*

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

## Phased implementation (the rest)

**F0 — Pane abstraction (the foundational, careful refactor).** Generalise `SessionTilePanel`'s cell content from session-specific to an `IPane` (`AiSessionPane | TerminalPane | WidgetPane`), `AiSessionPane` wrapping today's session VM with no behaviour change.
> ⚠️ **Leermoment 2026-07-13:** reorder must stay pure layout via `_cells` — never `Sessions.Move`, which rebuilt the pane and produced a black, pty-less terminal (proven then via a temporary TTY-LIFECYCLE log). Any grid change here needs the same care and a runtime check on a real display, which is why F0 was **not** done blind overnight — it is the first thing to pair on. AI-only header/permission/resume contributions become pane-kind-gated (they must not attach to terminal/widget panes).

**F1 — Terminal panes.** A `SessionKind.Terminal` (shell) that launches `pwsh`/`bash`/`wsl`/`cmd` in the existing pty + renderer instead of `claude`; shell picker + working dir (reuse the recent/favorites the New-session dialog already has). No AI chrome on the header. Cheapest visible win; reuses the whole pty stack.

**F2 — Typed workspaces.** `Workspace { Id, Name, Kind (Sessions|Dashboard), Panes[], gutter-state }` + a `WorkspaceManager`; the "Workspaces" strip; `+` → kind picker; rename/close; persistence + active workspace in cockpit.json. The kind is an invariant that gates which pane kinds and which `+` affordance/sidebar/empty-state show.

**F3 — Widget host (uses the contract above).** Render a `WidgetRegistration.CreateView` in a pane with the standard chrome (title, ↻ refresh → `RefreshRequested`, ⚙ config, resize, ✕); the "Add widget" gallery reads `host.Widgets`; per-instance config persisted via `IWidgetContext.Storage`.

**F4 — Bundled reference widgets.** Clock, System Monitor (CPU/RAM/disk), Git Status (reuse the git-status plugin data, follow the active session dir via `Sessions`), Notes — bundled like the existing bundled plugins, to prove the SDK end to end.

**F5 — Polish.** Config dialogs, refresh cadence, drag widgets between workspaces, empty states, shortcuts (Ctrl+K / next-workspace), store: widget-type plugins.

## Persistence sketch (cockpit.json)

```json
{ "activeWorkspaceId": "ws1",
  "workspaces": [
    { "id": "ws1", "name": "Sessions", "kind": "sessions", "panes": [
      { "id": "p1", "kind": "ai", "profileId": "personal", "span": 1 },
      { "id": "p2", "kind": "terminal", "shell": "pwsh", "cwd": "~/dev", "span": 1 } ] },
    { "id": "ws2", "name": "Dashboard", "kind": "dashboard", "panes": [
      { "id": "p3", "kind": "widget", "widgetId": "system-monitor.usage", "instanceId": "i1", "config": {}, "span": 1 } ] }
  ] }
```

## Non-goals / orthogonal

- Headless session layer (#68) and delegation (#67) are orthogonal — `IPane` and `SessionRuntime`/`SessionManager` do not conflict; a terminal could later be a headless target too.
- Multi-provider (#26): an AI pane stays profile-bound; terminals/widgets are provider-agnostic.
