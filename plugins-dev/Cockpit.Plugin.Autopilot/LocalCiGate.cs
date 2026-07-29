using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Autopilot;

/// <summary>
/// Asks the Local CI plugin whether this checkout has earned a pull request, if it is installed at all.
/// <para>
/// Addressed by manifest id and an agreed action string, which is how plugin intents work — so nothing here
/// references that plugin's types, and a cockpit without it answers nothing and a run delivers exactly as it did
/// before the gate existed. The identical helper in the GitHub pull-requests plugin is the same three strings for
/// the same reason: two assemblies that must not depend on each other cannot share the constant.
/// </para>
/// </summary>
internal static class LocalCiGate
{
    private const string PluginId = "local-ci";
    private const string Action = "pr-gate";

    /// <summary>
    /// The reason no pull request may be opened from <paramref name="directory"/>, or null when one may. The gate
    /// is off unless the operator switched it on for that checkout, and it decides for itself whether to offer
    /// them a way past — so an answer of "no" here has already been put to a person.
    /// </summary>
    public static async Task<string?> RefusalFor(ICockpitHost host, string directory)
    {
        if (!host.CanSendIntent(PluginId, Action))
        {
            return null;
        }

        var answer = await host.SendIntent(PluginId, Action, new Dictionary<string, string> { ["repository"] = directory });
        if (answer is null || answer.GetValueOrDefault("allowed") != "false")
        {
            return null;
        }

        return $"Local CI is holding the pull request back: {answer.GetValueOrDefault("reason")}";
    }
}
