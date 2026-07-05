using System.Collections.Concurrent;
using Cockpit.Core.Abstractions.Claude;
using Cockpit.Core.Claude.Permissions;

namespace Cockpit.Core.Tests.Claude;

/// <summary>
/// In-memory <see cref="IPermissionRuleStore"/> test double: keeps per-profile rules in a dictionary
/// so a test can assert what a session persisted (or preload rules for a session to start with),
/// without touching cockpit.json. A null profile label is the no-persistence case, mirroring the
/// real store.
/// </summary>
internal sealed class InMemoryPermissionRuleStore : IPermissionRuleStore
{
    private readonly ConcurrentDictionary<string, List<PermissionRule>> _rulesByProfile = new();

    public InMemoryPermissionRuleStore(string? profileLabel = null, params PermissionRule[] seed)
    {
        if (!string.IsNullOrEmpty(profileLabel) && seed.Length > 0)
        {
            _rulesByProfile[profileLabel] = seed.ToList();
        }
    }

    public IReadOnlyList<PermissionRule> RulesFor(string profileLabel) =>
        _rulesByProfile.TryGetValue(profileLabel, out var rules) ? rules.ToList() : [];

    public Task<IReadOnlyList<PermissionRule>> LoadAsync(string? profileLabel, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<PermissionRule> rules = string.IsNullOrEmpty(profileLabel)
            ? []
            : RulesFor(profileLabel);
        return Task.FromResult(rules);
    }

    public Task AddAsync(string? profileLabel, PermissionRule rule, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrEmpty(profileLabel))
        {
            var rules = _rulesByProfile.GetOrAdd(profileLabel, _ => []);
            if (!rules.Contains(rule))
            {
                rules.Add(rule);
            }
        }

        return Task.CompletedTask;
    }
}
