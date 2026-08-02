namespace Cockpit.Core.Abstractions.Consent;

/// <summary>
/// Decides whether the consent card may be skipped for one request (#AC-575). The seam between the consent broker,
/// which lives in Infrastructure and must not learn what an assistant is, and the one thing that knows both the
/// assistant's pane id and the operator's per-source switches.
/// </summary>
/// <remarks>
/// <b>Why the assistant may skip it at all.</b> The assistant is a speech surface: every host-side action it takes
/// (kubernetes, docker, a terminal command, a workflow, a plugin) raises the same card, and answering it means
/// walking to the screen and clicking — a hundred times over a session. The operator sets the bypass beforehand,
/// deliberately, with a click, which is still a visual consent; it is given once instead of a hundred times. That is
/// the whole of the trade, and it is a trade the operator makes knowingly, per source.
/// <para>
/// <b>What is deliberately not here.</b> No expiry — no "this session" / "today" / "permanently". A third axis
/// (source × risk × term) makes the setting unreadable, and the ticket does not ask for one. On or off per source,
/// and it stays as the operator left it until they turn it off.
/// </para>
/// <para>
/// <b>Fail closed.</b> The broker takes this as an optional dependency; with none registered nothing is ever
/// bypassed. An implementation that cannot answer must return <see langword="false"/> for the same reason.
/// </para>
/// <para>
/// <b>Why primitives rather than the <c>ConsentRequest</c>.</b> <c>ConsentRequest</c> lives in the plugin SDK
/// assembly, which this one does not reference — and must not, because a bypass is not something the plugin
/// surface should be able to see or ask for. The broker passes the three facts the decision may rest on, and
/// derives <paramref name="sourceKey"/> itself so the rule for what identifies a source lives next to the
/// remember-key rule it mirrors.
/// </para>
/// </remarks>
public interface IConsentBypassPolicy
{
    /// <summary>
    /// Whether this request may proceed without showing the card.
    /// </summary>
    /// <param name="verifiedPaneId">
    /// The <em>transport-verified</em> pane id of the request (<c>McpRequestContext.CurrentPaneId</c>), never the
    /// session the caller declared. Null for a request that arrived on no verified session at all — which is not an
    /// identity, so it can never be the assistant's.
    /// </param>
    /// <param name="sourceKey">
    /// Who asked, as a host-stamped identity: the plugin id when one asked through the host, otherwise the source's
    /// label, which is a compile-time constant in host code. Never the scope or the action — those are text an
    /// agent influences, and keying on them is how a bypass for one thing becomes a bypass for another.
    /// </param>
    /// <param name="dangerous">
    /// Whether the request is <c>ConsentRisk.Dangerous</c>. A dangerous action needs its own second switch for that
    /// source, off by default and never implied by the low-risk one.
    /// </param>
    bool ShouldBypass(string? verifiedPaneId, string sourceKey, bool dangerous);
}
