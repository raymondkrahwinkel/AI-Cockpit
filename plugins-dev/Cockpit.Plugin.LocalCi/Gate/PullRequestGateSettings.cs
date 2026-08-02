using Cockpit.Plugin.LocalCi.Execution;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.LocalCi.Gate;

// Which checkouts the pull-request gate is switched on for. Off everywhere until somebody turns it on, which is
// the setting AC-453 asks for: a gate that arrives switched on would hold back pull requests in every project the
// operator has, for a feature they have not tried yet.
// Kept as a list of checkouts in the plugin's own storage rather than as a field on the cockpit's project record.
// The host's per-project field is a chooser over options a plugin supplies, which is a poor fit for a switch, and
// the thing being gated is a repository — the callers that ask about it (a workflow step, an autopilot run) know
// a directory, not always a project.
internal sealed class PullRequestGateSettings(IPluginStorage storage)
{
    private const string Key = "prGate.checkouts";

    public IReadOnlyList<string> Checkouts =>
        storage.Get<List<string>>(Key) ?? [];

    public bool IsOnFor(string checkout) =>
        Checkouts.Contains(LocalRunTracker.Key(checkout), StringComparer.OrdinalIgnoreCase);

    public void Set(string checkout, bool on)
    {
        var key = LocalRunTracker.Key(checkout);
        var kept = Checkouts.Where(existing => !string.Equals(existing, key, StringComparison.OrdinalIgnoreCase)).ToList();

        if (on)
        {
            kept.Add(key);
        }

        storage.Set(Key, kept);
    }
}
