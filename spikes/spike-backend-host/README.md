# AC-1353 spike — the backend core without Cockpit.App

F0 of AC-1350. Not in `Cockpit.slnx`, never merged. The report is in Depot `cockpit`,
`Research/ac-1353-backend-zonder-ui.md`.

The host composes Core + Infrastructure the way `Program.Main` does (minus the App assembly), loads plugin entry
assemblies the way `PluginActivator` does, and prints one `SPIKE|phase|…` line per measurement.

```sh
# build the in-repo plugins once (Debug), then:
dotnet build spikes/spike-backend-host -c Debug

# every seam, every service, all plugins (Windows or Linux); always on a throwaway state root
COCKPIT_STATE_ROOT=<fresh dir> dotnet spikes/spike-backend-host/bin/Debug/net10.0/Cockpit.SpikeBackendHost.dll \
  --plugin plugins-dev/Cockpit.Plugin.X/bin/Debug/net10.0/Cockpit.Plugin.X.dll ...

# one Claude SDK turn through ISessionManager (unset the CLAUDE* variables of a surrounding session first)
SPIKE_CLAUDE_CONFIG_JSON='{"configDir":"…","executablePath":"…"}' … --session --plugin …ClaudeProvider.dll

# a scheduled Workflows flow (every 1m → delegate) observed for N minutes; --flow-off is the counter-check
… --flow 4 [--flow-off] --plugin …ClaudeProvider.dll --plugin …Workflows.dll

# which plugin-contract members carry an Avalonia type
… --contract

# Linux: dotnet publish -c Debug -r linux-x64 --self-contained false -o <dir>, then
docker run --rm -v <dir>:/app:ro -v <repo>/plugins-dev:/plugins:ro -e COCKPIT_STATE_ROOT=/state \
  mcr.microsoft.com/dotnet/sdk:10.0 dotnet /app/Cockpit.SpikeBackendHost.dll --plugin /plugins/…

# static UI-coupling sweep of Cockpit.App / the plugins
python spikes/spike-backend-host/sweep.py app|plugins
```

Flags: `--no-hosted` skips the hosted services, `--no-stubs` leaves the four App seams unfilled, `--jit-probe`
reports Avalonia loads around `typeof(ICockpitHost)` and a JIT of the plugin's `Initialize`.
