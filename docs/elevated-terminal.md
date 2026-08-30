# Administrator terminal (Windows)

Use **New administrator terminal (separate window)** from the `+ New` menu when a shell command needs administrator rights. Windows shows its normal UAC prompt. If it is declined, Cockpit starts nothing.

This is deliberately not a terminal pane. `ShellExecuteEx` with `runas` cannot attach the elevated process to Cockpit's ConPTY, so the window is visibly separate and Cockpit cannot read its transcript, show a statusline, list it as a session, or close it.

Cockpit grants no elevation to a session: this route exists only in the Windows operator UI, not in profile settings, session start requests, or Cockpit MCP tools. What a session does with its own full shell is governed by Windows UAC: a `runas` or `Start-Process -Verb RunAs` attempt shows the normal Windows prompt for the operator to judge. Cockpit neither removes nor bypasses that prompt. Broader control of what an agent session may invoke belongs to the command-policy work around AC-1235, not this route.

The separate process also cannot join Cockpit's Windows Job Object. Measured on 2026-08-30: after UAC elevation, opening the target PID with `PROCESS_SET_QUOTA | PROCESS_TERMINATE` from the non-elevated Cockpit process failed with `ERROR_ACCESS_DENIED` (5), while `PROCESS_QUERY_LIMITED_INFORMATION` succeeded. Closing Cockpit therefore cannot reliably terminate this administrator window; close it from the window itself.
