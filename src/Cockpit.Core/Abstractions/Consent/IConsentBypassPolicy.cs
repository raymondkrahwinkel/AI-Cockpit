namespace Cockpit.Core.Abstractions.Consent;

/// <summary>
/// Decides whether the consent card may be skipped for one request (#AC-575) — the seam between the consent broker
/// (Infrastructure, must not learn what an assistant is) and the one thing that knows both the assistant's pane id and the operator's per-source switches. Every host-side action raises the same card, so the operator sets the bypass beforehand, once per source, until turned off (no expiry). Fail closed: with none registered, or an implementation that cannot answer, nothing is bypassed. Takes primitives rather than <c>ConsentRequest</c> (plugin SDK, not referenced here) — the broker derives <paramref name="sourceKey"/> itself, next to the rule its remember-key mirrors.
/// </summary>
public interface IConsentBypassPolicy
{
    /// <summary>
    /// Whether this request may proceed without showing the card. <paramref name="verifiedPaneId"/> is the
    /// <em>transport-verified</em> pane id (<c>McpRequestContext.CurrentPaneId</c>), never the caller-declared
    /// session — null for no verified session, which can never be the assistant's. <paramref name="sourceKey"/> is a host-stamped identity (plugin id, or a compile-time source label), never agent-influenced scope/action text. <paramref name="dangerous"/> is <c>ConsentRisk.Dangerous</c>, its own switch, off by default, never implied by the low-risk one.
    /// </summary>
    bool ShouldBypass(string? verifiedPaneId, string sourceKey, bool dangerous);
}
