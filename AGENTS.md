# Repository instructions

Run `scripts/local-gate.sh --linux <test-project.csproj>` when a change touches files or I/O, processes, paths, logging, or platform-specific code. Pass normal `dotnet test` arguments such as `--filter` after the project path when narrowing a run.

The Linux port is also mandatory before restarting any failed nightly. It uses the .NET 10 SDK container and the nightly's build and test steps; a Windows-only green run is not enough for these changes.
