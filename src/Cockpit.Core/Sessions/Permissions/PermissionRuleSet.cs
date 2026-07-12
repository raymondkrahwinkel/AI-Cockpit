using Cockpit.Core.Abstractions.Sessions;

namespace Cockpit.Core.Sessions.Permissions;

/// <summary>
/// A profile's live set of always-allow rules. Backs the session's <see cref="IPermissionRuleChecker"/>
/// registration with the coordinator and grows as the operator picks "always allow" during the
/// session. Thread-safe for the concurrent add (operator thread) / check (MCP tool thread) it sees.
/// </summary>
public sealed class PermissionRuleSet : IPermissionRuleChecker
{
    private readonly object _gate = new();
    private readonly List<PermissionRule> _rules;

    public PermissionRuleSet(IEnumerable<PermissionRule>? rules = null)
    {
        _rules = rules?.ToList() ?? [];
    }

    /// <summary>A snapshot of the current rules, safe to enumerate/persist without holding the lock.</summary>
    public IReadOnlyList<PermissionRule> Snapshot()
    {
        lock (_gate)
        {
            return _rules.ToList();
        }
    }

    public bool IsAlwaysAllowed(string toolName, string proposedInputJson)
    {
        lock (_gate)
        {
            return _rules.Any(rule => rule.Matches(toolName, proposedInputJson));
        }
    }

    /// <summary>
    /// Adds <paramref name="rule"/> unless an equal one is already present. Returns true when the
    /// set actually changed, so the caller only persists on a real addition.
    /// </summary>
    public bool Add(PermissionRule rule)
    {
        lock (_gate)
        {
            if (_rules.Contains(rule))
            {
                return false;
            }

            _rules.Add(rule);
            return true;
        }
    }
}
