using Cockpit.Core.Profiles;

namespace Cockpit.Core.Sessions;

// How much one spawned session's whole process tree may hold before it is cut off (AC-661). Killing the session
// is the accepted outcome, so the number may be generous and blunt rather than tuned to a peak.
public static class SessionMemoryCap
{
    // A host key, like `cockpit.pane-id`: no provider declares it and no driver reads it.
    public const string OptionKey = "cockpit.memory-cap-mb";

    // Measured, not guessed: a full `dotnet test` of this repo peaks at ~3.1 GB across its tree (Windows,
    // 2026-08-09), so this leaves about 2.5× that.
    public const int DefaultMegabytes = 8192;

    // Below this the cap is likelier to kill the agent CLI itself than a runaway build.
    public const int MinimumMegabytes = 512;

    // The launch's own value wins over the profile's; below the floor is raised, not refused, since a session
    // that starts with a workable cap beats one that does not start.
    public static long ResolveBytes(SessionProfile? profile, IReadOnlyDictionary<string, string>? launchOptions) =>
        Megabytes(profile, launchOptions) * 1024L * 1024L;

    public static int Megabytes(SessionProfile? profile, IReadOnlyDictionary<string, string>? launchOptions)
    {
        var requested = _FromOptions(launchOptions) ?? profile?.MemoryCapMegabytes ?? DefaultMegabytes;
        return Math.Max(MinimumMegabytes, requested);
    }

    // So a typo in a spawn's options is reported rather than silently read as "no cap".
    public static string? RefusalFor(string value) =>
        int.TryParse(value, out var megabytes) && megabytes > 0
            ? null
            : $"'{value}' is not a memory cap. Give a whole number of megabytes, at least {MinimumMegabytes}.";

    private static int? _FromOptions(IReadOnlyDictionary<string, string>? launchOptions) =>
        launchOptions is not null
        && launchOptions.TryGetValue(OptionKey, out var value)
        && int.TryParse(value, out var megabytes)
        && megabytes > 0
            ? megabytes
            : null;
}
