namespace Cockpit.Core.Configuration;

// Which build this is, and the state directory that follows from it (AC-3).
// A development build keeps its state beside the production one rather than in it. This app is developed from
// sessions the production cockpit itself hosts, so a `dotnet run` and the cockpit the operator is actually
// using are routinely open at the same time — and until now they shared one `cockpit.json`, one plugins
// directory and one log. A half-built profile or a plugin registration from a debug run is not something the
// operator asked for, and the two racing each other over the same config is a corruption the config layer has
// already been bitten by. Separate roots mean neither can write over the other's state at all.
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
