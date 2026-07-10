namespace Cockpit.Infrastructure.Configuration;

/// <summary>Resolves the default <c>cockpit.json</c> location: <c>%APPDATA%\Cockpit\cockpit.json</c> on Windows.</summary>
internal static class CockpitConfigPath
{
    public static string Default => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Cockpit",
        "cockpit.json");

    /// <summary>The plugins root — a <c>plugins/</c> folder next to <c>cockpit.json</c>, stable across app updates. Each plugin lives in its own subfolder here.</summary>
    public static string PluginsRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Cockpit",
        "plugins");
}
