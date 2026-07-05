namespace Cockpit.Infrastructure.Configuration;

/// <summary>Resolves the default <c>cockpit.json</c> location: <c>%APPDATA%\Cockpit\cockpit.json</c> on Windows.</summary>
internal static class CockpitConfigPath
{
    public static string Default => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Cockpit",
        "cockpit.json");
}
