namespace Cockpit.Plugins.Abstractions.Capabilities;

/// <summary>
/// What granting a capability costs the operator, decided once at grant time rather than per call.
/// Mirrors <see cref="Consent.ConsentRisk"/> without being it: that enum answers "may this approve be
/// remembered", this one answers "how much does saying yes hand over".
/// </summary>
/// <remarks>
/// Deliberately three tiers where <see cref="Consent.ConsentRisk"/> has two. Collapsing
/// <see cref="Ambient"/> and <see cref="Sensitive"/> into one "low risk" bucket would put drawing a menu
/// button and reading every project's memory on the same line of a grant dialog. Only <see cref="Dangerous"/>
/// lines up one-to-one with <see cref="Consent.ConsentRisk.Dangerous"/>.
/// </remarks>
public enum CapabilityRisk
{
    /// <summary>
    /// The plugin adds to the cockpit's own surface and the operator sees the result. Nothing of theirs is read and nothing runs on their behalf.
    /// </summary>
    Ambient,

    /// <summary>
    /// The plugin reads or writes state it did not create — the operator's projects, sessions, profiles, repositories, credentials.
    /// </summary>
    Sensitive,

    /// <summary>
    /// The plugin acts with the operator's rights or opens egress: starting or steering agents, running a fetched executable, arbitrary MCP calls.
    /// </summary>
    Dangerous,
}
