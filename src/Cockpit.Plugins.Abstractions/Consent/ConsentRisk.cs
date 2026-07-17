namespace Cockpit.Plugins.Abstractions.Consent;

/// <summary>
/// How much a consented action can hurt if it rides along on a later, injected call — the one thing the broker
/// decides differently on. A <see cref="Dangerous"/> action (a shell command, starting or steering a session
/// with the operator's rights, arbitrary egress) is asked afresh every time: it is never remembered, because one
/// approve must not become a standing permission a prompt-injected agent can reuse. A <see cref="LowRisk"/>,
/// idempotent action may offer "remember for this session".
/// </summary>
public enum ConsentRisk
{
    LowRisk,

    Dangerous,
}
