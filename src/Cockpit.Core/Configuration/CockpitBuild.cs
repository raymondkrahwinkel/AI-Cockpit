namespace Cockpit.Core.Configuration;

/// <summary>
/// Which build this is, and the state directory that follows from it (AC-3).
/// </summary>
/// <remarks>
/// A development build keeps its state beside the production one rather than in it. This app is developed from
/// sessions the production cockpit itself hosts, so a <c>dotnet run</c> and the cockpit the operator is actually
/// using are routinely open at the same time — and until now they shared one <c>cockpit.json</c>, one plugins
/// directory and one log. A half-built profile or a plugin registration from a debug run is not something the
/// operator asked for, and the two racing each other over the same config is a corruption the config layer has
/// already been bitten by. Separate roots mean neither can write over the other's state at all.
/// </remarks>
public static class CockpitBuild
{
    public const string ProductionStateFolder = "Cockpit";
    public const string DevelopmentStateFolder = "Cockpit-Dev";

    /// <summary>
    /// True for a Debug build — what <c>dotnet run</c> produces and what nobody installs. This is the one line
    /// here that no test can prove, because a test run only ever compiles one arm of it.
    /// </summary>
    public static bool IsDevelopment =>
#if DEBUG
        true;
#else
        false;
#endif

    /// <summary>The folder this build keeps its state in, under the platform's application-data directory.</summary>
    public static string StateFolder => IsDevelopment ? DevelopmentStateFolder : ProductionStateFolder;

    /// <summary>
    /// The root of everything this build persists: config, plugins, logs, caches. Every writer resolves its path
    /// from here, so which build is running is decided once rather than by each caller rebuilding the path.
    /// </summary>
    public static string StateRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        StateFolder);
}
