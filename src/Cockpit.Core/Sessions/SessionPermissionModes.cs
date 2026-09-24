namespace Cockpit.Core.Sessions;

// AC-1379: the two CLI --permission-mode values the assistant's host reasons about, shared with the app's option
// catalog so the host outside the app and the dropdown inside it cannot drift apart.
public static class SessionPermissionModes
{
    public const string Default = "default";

    public const string Bypass = "bypassPermissions";
}
