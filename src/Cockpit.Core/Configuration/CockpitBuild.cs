namespace Cockpit.Core.Configuration;

// Development and production builds use separate state roots (AC-3), as both commonly run together.
// This prevents debug profiles, plugins, logs, or concurrent config writes corrupting the operator's production state.
// In normal development, production Cockpit hosts the session that runs the debug build, so coexistence is routine.
public static class CockpitBuild
{
    public const string ProductionStateFolder = "Cockpit";
    public const string DevelopmentStateFolder = "Cockpit-Dev";

    // Points this instance's whole state root somewhere else (AC-1214). A doctored `APPDATA` cannot do this:
    // `GetFolderPath` reads the shell folder from the registry and ignores that variable on Windows.
    public const string StateRootVariable = "COCKPIT_STATE_ROOT";

    // True for a Debug build — what `dotnet run` produces and what nobody installs. This is the one line
    // here that no test can prove, because a test run only ever compiles one arm of it.
    public static bool IsDevelopment =>
#if DEBUG
        true;
#else
        false;
#endif

    // The folder this build keeps its state in, under the platform's application-data directory.
    public static string StateFolder => IsDevelopment ? DevelopmentStateFolder : ProductionStateFolder;

    // Where this build's state lives when nothing overrides it. Named because the single-instance claim has to
    // tell "the state root every install shares" from "a root this instance was pointed at" (AC-1217).
    public static string DefaultStateRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), StateFolder);

    // The root of everything this build persists, resolved here alone. A half-isolated instance is worse than
    // none: it reads as isolated while one path still writes into the operator's real state.
    public static string StateRoot =>
        Environment.GetEnvironmentVariable(StateRootVariable) is { } stateRoot && !string.IsNullOrWhiteSpace(stateRoot)
            ? RequireFullyQualified(stateRoot)
            : DefaultStateRoot;

    // The file this build logs to. Resolved here rather than at the point of writing because more than one thing
    // now needs to name it: the logger opens it, and the diagnostics report tells the operator where it is — every
    // message that says "see the log" is worth only as much as their ability to find it.
    public static string LogPath => Path.Combine(StateRoot, "logs", "cockpit.log");

    // A relative override would resolve against each process's working directory, and the cockpit gives its
    // children their own — so the desktop and a session it spawned would isolate to two different places while
    // reading as one. Refusing at the first read makes that a startup failure instead of a silent half-isolation.
    private static string RequireFullyQualified(string stateRoot) =>
        Path.IsPathFullyQualified(stateRoot)
            ? stateRoot
            : throw new InvalidOperationException(
                $"{StateRootVariable} must be an absolute path; it is \"{stateRoot}\".");
}
