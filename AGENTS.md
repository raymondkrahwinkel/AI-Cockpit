# Repository instructions

Run `scripts/local-gate.sh --linux <test-project.csproj>` when a change touches files or I/O, processes, paths, logging, or platform-specific code. Pass normal `dotnet test` arguments such as `--filter` after the project path when narrowing a run.

The Linux port is also mandatory before restarting any failed nightly. It uses the .NET 10 SDK container and the nightly's build and test steps; a Windows-only green run is not enough for these changes.

When adding `cockpit.json` setting keys, document them in `README.md`, add staged Options values to `OptionsStaging`, and decide explicitly whether each key belongs in `SettingsCarrierWhitelist`.

The Browser plugin stores `agentNetworkAllowlist` as a global string array through `IPluginStorage`.
Entries are hosts, hosts with ports (IPv6 with a port uses `[::1]:8080`), or CIDRs, validated before saving.
Explicit entries override the internal network deny for agent-owned tabs; the operator's UI browser is unaffected.
