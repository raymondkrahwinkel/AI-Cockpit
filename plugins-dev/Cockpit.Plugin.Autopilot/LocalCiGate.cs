using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Autopilot;

// Asks the Local CI plugin whether this checkout has earned a pull request, if it is installed at all. Addressed
// by manifest id and an agreed action string — nothing here references that plugin's types, so a cockpit
// without it answers nothing and a run delivers exactly as before.
internal static class LocalCiGate
{
    private const string PluginId = "local-ci";
    private const string Action = "pr-gate";

    // The reason no pull request may be opened from `directory`, or null when one may. The gate
    // is off unless the operator switched it on for that checkout, and it decides for itself whether to offer
    // them a way past — so an answer of "no" here has already been put to a person.
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
