# Administrator terminal (Windows)

Use **New administrator terminal (separate window)** from the `+ New` menu when a shell command needs administrator rights. Windows shows its normal UAC prompt. If it is declined, Cockpit starts nothing.

This is deliberately not a terminal pane. `ShellExecuteEx` with `runas` cannot attach the elevated process to Cockpit's ConPTY, so the window is visibly separate and Cockpit cannot read its transcript, show a statusline, list it as a session, or close it.

Cockpit exposes this launch route only through the Windows operator UI: it has no elevation setting on profiles or session start requests, and no Cockpit MCP tool. A separately authorised agent process with general local shell permission can still call Windows UAC itself; preventing that needs a broader command-policy/security design and is outside this short-term route.

The separate process also cannot join Cockpit's Windows Job Object. Measured on 2026-08-30: after UAC elevation, opening the target PID with `PROCESS_SET_QUOTA | PROCESS_TERMINATE` from the non-elevated Cockpit process failed with `ERROR_ACCESS_DENIED` (5), while `PROCESS_QUERY_LIMITED_INFORMATION` succeeded. Closing Cockpit therefore cannot reliably terminate this administrator window; close it from the window itself.
