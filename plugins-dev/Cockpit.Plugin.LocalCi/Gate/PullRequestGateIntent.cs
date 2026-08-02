using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Consent;

namespace Cockpit.Plugin.LocalCi.Gate;

// The gate as something another plugin can ask about (AC-95 intents). Whoever is about to open a pull request
// sends `Action` with the repository, and gets back whether to go ahead.
//
// An intent rather than a tool an agent calls, because a gate an agent may skip is a suggestion. The two places
// that actually open a pull request ask this before they do, and a cockpit without this plugin installed answers
// nothing — `CanSendIntent` tells them so, and they carry on exactly as they did.
internal sealed class PullRequestGateIntent(ICockpitHost host, PullRequestGate gate)
{
    public const string Action = "pr-gate";

    // The checkout the pull request would be opened from. Without it there is nothing to judge.
    public const string RepositoryKey = "repository";

    // "true" or "false" — the only key a caller has to read.
    public const string AllowedKey = "allowed";

    public const string StatusKey = "status";

    public const string ReasonKey = "reason";

    public async Task<IReadOnlyDictionary<string, string>> HandleAsync(PluginIntent intent)
    {
        if (!intent.Data.TryGetValue(RepositoryKey, out var checkout) || checkout.Length == 0)
        {
            // Nothing was named, so nothing was gated. Refusing here would hold back pull requests over a payload
            // detail rather than over a check, which is not what the operator switched on.
            return _Answer(allowed: true, "no-repository", "no repository was named, so there was nothing to check.");
        }

        var verdict = await gate.JudgeAsync(checkout, CancellationToken.None);
        if (verdict.AllowsWithoutAsking)
        {
            return _Answer(allowed: true, verdict.Status.ToString().ToLowerInvariant(), verdict.Reason);
        }

        // The explicit way past. It goes through the host's consent, which means the operator sees the reason and
        // the decision lands in the consent trail — that is where "who waved this through, and why" is answerable
        // afterwards, and it is the only reason a bypass is allowed to exist at all.
        var bypass = await host.RequestConsentAsync(new ConsentRequest(
            Title: "Local CI has not passed for this checkout",
            Action: $"Open a pull request from {checkout} without a passing local run.\n\nWhy it is held back: {verdict.Reason}",
            Source: new ConsentSource(PaneId: null, PluginId: null, "Local CI"),
            Scope: "local-ci.pr-gate",
            Risk: ConsentRisk.Dangerous));

        return bypass.IsApproved
            ? _Answer(allowed: true, "bypassed", verdict.Reason)
            : _Answer(allowed: false, verdict.Status.ToString().ToLowerInvariant(), verdict.Reason);
    }

    private static Dictionary<string, string> _Answer(bool allowed, string status, string reason) => new()
    {
        [AllowedKey] = allowed ? "true" : "false",
        [StatusKey] = status,
        [ReasonKey] = reason,
    };
}
