namespace Cockpit.Core.Abstractions.Consent;

/// <summary>
/// Decides whether the consent card may be skipped for one request (#AC-575) — the seam between the consent broker
/// (Infrastructure, must not learn what an assistant is) and the one thing that knows both the assistant's pane id
/// and the operator's per-source switches. The assistant is a speech surface: every host-side action raises the
/// same card, so the operator sets the bypass beforehand, deliberately, once instead of a hundred times, per
/// source. No expiry axis — on/off per source only, until the operator turns it off. Fail closed: with none
/// registered, or an implementation that cannot answer, nothing is ever bypassed. Takes primitives rather than
/// <c>ConsentRequest</c> (plugin SDK, not referenced here, and must not be) — the broker passes the three facts and
/// derives <paramref name="sourceKey"/> itself, next to the remember-key rule it mirrors.
/// </summary>
public interface IConsentBypassPolicy
{
    /// <summary>
    /// Whether this request may proceed without showing the card. <paramref name="verifiedPaneId"/> is the
    /// <em>transport-verified</em> pane id (<c>McpRequestContext.CurrentPaneId</c>), never the caller-declared
    /// session — null for no verified session, which can never be the assistant's. <paramref name="sourceKey"/> is
    /// a host-stamped identity (plugin id, or a compile-time source label), never the scope/action text an agent
    /// influences. <paramref name="dangerous"/> is <c>ConsentRisk.Dangerous</c>, needing its own switch, off by
    /// default and never implied by the low-risk one.
    /// </summary>
    bool ShouldBypass(string? verifiedPaneId, string sourceKey, bool dangerous);
}
