namespace Cockpit.Plugins.Abstractions.Consent;

/// <summary>What the operator decided about a <see cref="ConsentRequest"/>.</summary>
public enum ConsentOutcome
{
    /// <summary>Go ahead — the caller may perform the action.</summary>
    Approved,

    /// <summary>Do not. Also the answer when nothing could ask (no consent surface) or the request was cancelled — the broker fails closed.</summary>
    Denied,
}
