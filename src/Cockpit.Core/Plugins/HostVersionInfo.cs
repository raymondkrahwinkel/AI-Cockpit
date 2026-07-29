namespace Cockpit.Core.Plugins;

/// <summary>
/// This cockpit's own running version — the one figure every plugin-compatibility gate (load, install, and
/// the store browse badge, AC-181) measures a <c>minHostVersion</c> against, read once from the entry
/// assembly so the three can never drift apart and disagree with each other.
/// </summary>
public static class HostVersionInfo
{
    public static Version Current { get; } =
        System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0);
}
