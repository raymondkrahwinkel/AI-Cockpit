using System.Runtime.InteropServices;

namespace Cockpit.Plugin.ClaudeProvider;

// AC-1468: the server image runs claude under its own uid (AC-1464), which shares one group with the cockpit. The
// image names that group's numeric gid in COCKPIT_AGENT_GROUP, and the files this plugin hands the CLI by path (the
// mcp-config, the prompt file, the statusline relay) are then opened to that group, and to nobody else. Unset, which
// is every desktop, nothing here acts and the owner-only modes stay exactly as they were. A value that is not a gid,
// or a chgrp the kernel refuses, also leaves the owner-only mode: the session then misses its files, which shows,
// rather than having a secret opened to a group nobody chose.
internal static class ClaudeAgentGroup
{
    public const string EnvironmentVariable = "COCKPIT_AGENT_GROUP";

    public static int? Gid { get; } = Parse(Environment.GetEnvironmentVariable(EnvironmentVariable));

    internal static int? Parse(string? value) =>
        !OperatingSystem.IsWindows() && int.TryParse(value, out var gid) && gid >= 0 ? gid : null;

    // Hands `path` to the agent group and then widens it to `mode`, in that order, so the content is never readable
    // by the group the file happened to be created with. False, with the path untouched, when no group is set.
    public static bool Share(string path, UnixFileMode mode) => Share(path, mode, Gid);

    internal static bool Share(string path, UnixFileMode mode, int? gid)
    {
        if (gid is not { } group || OperatingSystem.IsWindows() || chown(path, -1, group) != 0)
        {
            return false;
        }

        try
        {
            File.SetUnixFileMode(path, mode);
            return true;
        }
        catch (Exception)
        {
            // The group changed but the mode did not: still owner-only, so nothing is opened. Same tolerance as the
            // callers' own chmod for a filesystem without Unix permissions.
            return false;
        }
    }

    // .NET has no managed chgrp; an owner of -1 leaves the owner as it is.
    [DllImport("libc", SetLastError = true)]
    private static extern int chown([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int owner, int group);
}
