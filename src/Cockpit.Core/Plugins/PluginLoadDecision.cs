namespace Cockpit.Core.Plugins;

// What the host should do with a discovered plugin — the pure outcome of `PluginLoadPolicy.Decide`.
public enum PluginLoadDecision
{
    // Enabled, hash matches the pinned consent, abstractions major matches — load it.
    Load,

    // Known but the operator disabled it — skip.
    Disabled,

    // Never seen before, or the assembly hash changed since consent — prompt before loading.
    NeedsConsent,

    // Built against a different Cockpit.Plugins.Abstractions major than the host — refuse with a clear message.
    AbstractionsMajorMismatch,

    // Needs a newer cockpit than this one (its `minHostVersion`) — refuse rather than load something that will fail where nobody can see it.
    HostTooOld,
}
