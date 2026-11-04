namespace Cockpit.App.Plugins;

// AC-2025: a server plugin's settings run through this desktop's copy of its UI part, so the two have to be the same
// version. A live update makes a difference likelier, and a different contract must be said, not guessed through.
internal static class ServerPluginVersions
{
    public static bool Differ(string local, string server) => !string.Equals(local, server, StringComparison.Ordinal);

    public static string Message(string plugin, string server, string serverVersion, string localVersion) =>
        $"{plugin} is version {serverVersion} on {server} and {localVersion} on this desktop. Update the older one so they match; until then its settings are not opened here.";
}
