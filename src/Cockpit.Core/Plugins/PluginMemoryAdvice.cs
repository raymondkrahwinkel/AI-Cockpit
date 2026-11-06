namespace Cockpit.Core.Plugins;

// AC-2025: a plugin updated while the host runs leaves its previous version in memory (the contexts are not collectible);
// past a number of those the plugin screens, on the desktop and for a server, advise a restart.
public static class PluginMemoryAdvice
{
    public const string Title = "Restart to free memory";

    public static bool IsDue(int retired, int after) => retired > 0 && retired >= after;

    public static string Detail(int retired) =>
        $"{retired} replaced plugin version{(retired == 1 ? "" : "s")} stay in memory until the next start.";
}
