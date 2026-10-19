# AC-1576 spike — not product code

The report is in Depot `cockpit` › `Research/AC-1576-spike.md`. None of these projects is in `Cockpit.slnx`, and none of them should be merged.

- `NativeYjs/`: a .NET Yjs client (YDotNet 0.6.0 over yrs) on Depot's `/hubs/notes`. You drive it by sending lines on stdin (`type`, `para`, `offline`, `online`, `dump`, `quit`). `--v2` is the counter-proof.
- `Journey/`: points 1 to 3 of the ticket against a local Depot of `epic/DEP-385`. It runs a web tab (Playwright) and `NativeYjs` side by side and exits 0 only when every check passes.
- `WebViewProbe/`: the Depot editor inside Avalonia's `NativeWebView`. A stand-in for the plugin backend signs in and gives the view only the HttpOnly session cookie.

Run the journey against a local Depot whose admin password is in `<pwfile>`. `PW_CHROMIUM` points at an installed Chromium if Playwright's own build is missing.

```
dotnet build spikes/AC-1576/NativeYjs && dotnet build spikes/AC-1576/Journey
dotnet spikes/AC-1576/Journey/bin/Debug/net10.0/Journey.dll http://localhost:5062 <pwfile> spikes/AC-1576/NativeYjs/bin/Debug/net10.0/NativeYjs.dll [--v2]
```

The journey expects the organization `wispslate` and a project `spike` in that Depot.
