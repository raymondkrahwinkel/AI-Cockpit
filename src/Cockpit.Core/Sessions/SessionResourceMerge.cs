using Cockpit.Core.Sessions.Tty;

namespace Cockpit.Core.Sessions;

/// <summary>
/// Folds what several plugins contribute to one starting session into a single answer (AC-165). Pure and separate
/// from the resolver that gathers the contributions, so the rules below can be tested without standing up a plugin
/// host — the same reason <c>McpServerCatalog.Merge</c> is its own method.
/// </summary>
public static class SessionResourceMerge
{
    /// <summary>
    /// The contributions as one environment: each key kept as the first contributor that set it left it, and every
    /// host-controlled key dropped.
    /// <para>
    /// First one wins rather than last, because the alternative is that a session's environment depends on the
    /// order plugins happened to load — which changes when the operator installs an unrelated one. Two plugins
    /// setting the same variable is not an error either: it usually means they agree about it.
    /// </para>
    /// <para>
    /// Scrubbed here even though the caller is expected to have scrubbed already, so the rule holds where the value
    /// is used rather than where it was collected. A guard that relies on its caller having run is not a guard.
    /// </para>
    /// </summary>
    /// <param name="contributions">What each plugin returned, in the order the plugins were asked.</param>
    /// <returns>The merged resources, and the keys that were refused — names only, never values.</returns>
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
