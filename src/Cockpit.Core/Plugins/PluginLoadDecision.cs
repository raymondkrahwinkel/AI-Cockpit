namespace Cockpit.Core.Plugins;

/// <summary>What the host should do with a discovered plugin — the pure outcome of <see cref="PluginLoadPolicy.Decide"/>.</summary>
public enum PluginLoadDecision
{
    /// <summary>Enabled, hash matches the pinned consent, abstractions major matches — load it.</summary>
    Load,

    /// <summary>Known but the operator disabled it — skip.</summary>
    Disabled,

    /// <summary>Never seen before, or the assembly hash changed since consent — prompt before loading.</summary>
    NeedsConsent,

    /// <summary>Built against a different Cockpit.Plugins.Abstractions major than the host — refuse with a clear message.</summary>
    AbstractionsMajorMismatch,

    /// <summary>Needs a newer cockpit than this one (its <c>minHostVersion</c>) — refuse rather than load something that will fail where nobody can see it.</summary>
    HostTooOld,
}
