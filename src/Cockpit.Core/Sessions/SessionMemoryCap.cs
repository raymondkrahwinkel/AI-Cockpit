using Cockpit.Core.Profiles;

namespace Cockpit.Core.Sessions;

// How much memory one spawned session's whole process tree may hold before the OS cuts it off (AC-661).
//
// The cap exists for one reason: keep a runaway child — a `dotnet test` that blows up — from taking the cockpit
// down with it. Killing the session is the accepted outcome, not the thing to avoid, so the number may be
// generous and blunt rather than measured against a peak.
public static class SessionMemoryCap
{
    // The launch-option key a spawn names to override the profile's cap for one session — a host key, like
    // `cockpit.pane-id`: no provider declares it and no driver reads it, the host applies it to the OS.
    public const string OptionKey = "cockpit.memory-cap-mb";

    // Measured, not guessed: a full `dotnet test Cockpit.slnx -warnaserror` of this repo peaks at ~3.1 GB across
    // its whole process tree (Windows, 2026-08-09), so this leaves a session about 2.5× that before it is cut off.
    public const int DefaultMegabytes = 8192;

    // Under this a cap is more likely to kill the agent CLI itself than a runaway build, which would read as
    // "the cockpit is broken" rather than "that run was too big".
    public const int MinimumMegabytes = 512;

    // Resolved per session: what the launch asked for wins over what the profile was configured with, and the
    // default stands when neither says anything. A value below the floor is raised to it rather than refused —
    // a session that starts with a cap it can live with beats one that does not start.
    public static long ResolveBytes(SessionProfile? profile, IReadOnlyDictionary<string, string>? launchOptions) =>
        Megabytes(profile, launchOptions) * 1024L * 1024L;

    public static int Megabytes(SessionProfile? profile, IReadOnlyDictionary<string, string>? launchOptions)
    {
        var requested = _FromOptions(launchOptions) ?? profile?.MemoryCapMegabytes ?? DefaultMegabytes;
        return Math.Max(MinimumMegabytes, requested);
    }

    // Rejects a value that is not a positive whole number of megabytes, so a typo in a spawn's options is not
    // silently read as "no cap" — the caller reports the refusal instead of launching uncapped.
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
