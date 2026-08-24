namespace Cockpit.Core.Plugins;

// The pure decision for a discovered plugin: the abstractions-major gate first (a mismatch is refused
// no matter what), then the consent/enabled/hash state. No IO — the caller supplies the freshly computed
// assembly hash and the saved registration (null when the plugin has never been seen).
public static class PluginLoadPolicy
{
    public static PluginLoadDecision Decide(
        PluginManifest manifest,
        int hostAbstractionsMajor,
        PluginRegistration? saved,
        string currentSha256,
        Version? hostVersion = null)
    {
        if (manifest.AbstractionsVersion != hostAbstractionsMajor)
        {
            return PluginLoadDecision.AbstractionsMajorMismatch;
        }

        // AC-1013: contract-major alone misses a plugin that calls a member the host lacks yet;
        // minHostVersion is the only gate that catches it, and previously nothing compared it.
        // (Omitted: history of every manifest claiming template-default 1.0.0; see ticket.)
        if (!MeetsMinHostVersion(manifest.MinHostVersion, hostVersion))
        {
            return PluginLoadDecision.HostTooOld;
        }

        if (saved is null)
        {
            return PluginLoadDecision.NeedsConsent;
        }

        if (!saved.Enabled)
        {
            return PluginLoadDecision.Disabled;
        }

        return string.Equals(saved.PinnedSha256, currentSha256, StringComparison.OrdinalIgnoreCase)
            ? PluginLoadDecision.Load
            : PluginLoadDecision.NeedsConsent;
    }

    // AC-181/AC-1013: shared by every gate comparing a plugin to this host. A declared "1.0.0+" on a
    // sub-1.0 host is ignored as the unenforced template default, but an honest sub-1.0 requirement is
    // enforced; unparsable/missing means "nothing declared", not refused. (Omitted: Versioning-skill and 21+-manifest evidence; see ticket.)
    public static bool MeetsMinHostVersion(string? minHostVersion, Version? hostVersion)
    {
        if (hostVersion is null || !Version.TryParse(minHostVersion, out var required))
        {
            return true;
        }

        if (hostVersion.Major < 1 && required.Major >= 1)
        {
            return true;
        }

        return hostVersion >= required;
    }
}
