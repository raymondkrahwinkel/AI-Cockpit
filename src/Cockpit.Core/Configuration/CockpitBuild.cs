namespace Cockpit.Core.Configuration;

// Development and production builds use separate state roots (AC-3), as both commonly run together.
// This prevents debug profiles, plugins, logs, or concurrent config writes corrupting the operator's production state.
// In normal development, production Cockpit hosts the session that runs the debug build, so coexistence is routine.
public static class CockpitBuild
{
    public const string ProductionStateFolder = "Cockpit";
    public const string DevelopmentStateFolder = "Cockpit-Dev";

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

    // The root of everything this build persists: config, plugins, logs, caches. Every writer resolves its path
    // from here, so which build is running is decided once rather than by each caller rebuilding the path.
    public static string StateRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        StateFolder);

    // The file this build logs to. Resolved here rather than at the point of writing because more than one thing
    // now needs to name it: the logger opens it, and the diagnostics report tells the operator where it is — every
    // message that says "see the log" is worth only as much as their ability to find it.
    public static string LogPath => Path.Combine(StateRoot, "logs", "cockpit.log");
}
