using Cockpit.Core.Sessions.Tty;

namespace Cockpit.Core.Sessions;

// Folds what several plugins contribute to one starting session into a single answer (AC-165). Kept apart from the
// resolver that gathers the contributions so the rules below can be tested without standing up a plugin host.
public static class SessionResourceMerge
{
    // Merges contributions into one environment, first contributor wins (else a session's environment would
    // depend on plugin load order), every host-controlled key dropped. Scrubbed here too, not just at the
    // caller, so the rule holds where the value is used — a guard relying on its caller having run isn't one.
    public static (SessionResources Resources, IReadOnlyList<string> RejectedEnvironmentKeys) Merge(
        IReadOnlyList<SessionResources> contributions)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        var rejected = new List<string>();

        foreach (var variable in contributions.SelectMany(contribution => contribution.EnvironmentVariables))
        {
            if (TtyEnvironment.IsHostControlled(variable.Key))
            {
                rejected.Add(variable.Key);
                continue;
            }

            environment.TryAdd(variable.Key, variable.Value);
        }

        return environment.Count == 0
            ? (SessionResources.Empty, rejected)
            : (new SessionResources(environment), rejected);
    }
}
