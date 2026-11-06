# Repository instructions

Run `scripts/local-gate.sh --linux <test-project.csproj>` when a change touches files or I/O, processes, paths, logging, or platform-specific code. Pass normal `dotnet test` arguments such as `--filter` after the project path when narrowing a run.

The Linux port is also mandatory before restarting any failed nightly. It uses the .NET 10 SDK container and the nightly's build and test steps; a Windows-only green run is not enough for these changes.

The test-listener loopback guard permits AC-2029's exact opt-in wildcard prefix only inside `OwnedTabFixture(bool acceptAnyHost = false)` in `plugins-dev/Cockpit.Plugin.Browser.Tests/AgentOwnedTabTests.cs`, where Linux must accept an unresolved DNS-rebinding Host header. Other wildcard listeners remain forbidden.

Local CI MCP tools resolve the calling pane's checkout from the backend. A background pane does not need to be opened in the UI before it can run or inspect local checks.

When adding `cockpit.json` setting keys, document them in `README.md`, add staged Options values to `OptionsStaging`, and decide explicitly whether each key belongs in `SettingsCarrierWhitelist`.

The Browser plugin stores `agentNetworkAllowlist` as a global string array through `IPluginStorage`.
Entries are hosts, hosts with ports (IPv6 with a port uses `[::1]:8080`), or CIDRs, validated before saving.
Explicit entries override the internal network deny for agent-owned tabs; the operator's UI browser is unaffected.

## Tests

The rules are Testregels v2 in Depot `krahwinkel-it/cockpit`, `Conventions/Testing.md`. They take precedence over everything else in that document.

- A test may exist only when it guards something that can really go wrong and is not covered yet, under one of: **A** security and authorisation, **B** data loss (one round trip per kind of store), **C** concurrency, **D** an external contract, **E** a calculation or domain rule (tested with an example from the source), **F** non-trivial UI behaviour that decides something.
- Never: **X1** appearance (renders show that), **X2** passing settings through or mapping, **X3** trivial guards, **X4** repeating the implementation (a constant, a text, a fixed order), **X5** duplicates, **X6** more than three architecture or text-search tests, or MemberData per file.
- A ticket adds 0 tests by default, at most 1, preferably by extending an existing test. A PR names the category A–F of each new test with one sentence why.
- Journeys cover the nine critical paths only: Startup, FirstStart, Restore, Restart, Server, NodeConnect, RemoteAdmin, the basic Session and LinuxSmoke (AC-2040/AC-2065: the nightly starts the packaged AppImage without a GPU and opens every shipped surface; `scripts/linux-smoke.sh`, not an xUnit test; its clicks are fixed coordinates, so a change that moves a surface updates the step list in `scripts/linux-smoke/run.sh`). A new journey needs Raymond's approval.
- `scripts/test-count-baseline.txt` holds a hard ceiling of test cases per project, checked by `scripts/check-test-count.py` against `origin/main`. A ceiling only goes down: lower it in the PR that removes tests (`--update-baseline` refuses a rise). Raising one needs Raymond's approval, stated in the PR.
