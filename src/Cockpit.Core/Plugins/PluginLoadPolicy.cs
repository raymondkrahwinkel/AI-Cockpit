namespace Cockpit.Core.Plugins;

/// <summary>
/// The pure decision for a discovered plugin: the abstractions-major gate first (a mismatch is refused
/// no matter what), then the consent/enabled/hash state. No IO — the caller supplies the freshly computed
/// assembly hash and the saved registration (null when the plugin has never been seen).
/// </summary>
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

        // The contract major above only catches a plugin built against a different SDK generation. It says nothing
        // about a plugin that calls a member this host does not have yet — that one loads (the member exists in the
        // contract it compiled against) and then fails somewhere the operator cannot see. minHostVersion is the only
        // thing that catches it, and nothing compared it: every manifest could claim whatever it liked, and every
        // one of them claimed 1.0.0 because that is what the template said.
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

    /// <summary>
    /// True when a declared <c>minHostVersion</c> does not refuse <paramref name="hostVersion"/> — shared by every
    /// gate that measures a plugin against this host (AC-181: this load gate, the install-time gate, and the store
    /// browse "not compatible" badge), so the three can never disagree about the same plugin.
    /// <para>
    /// A plugin cannot honestly need a 1.0+ host while the cockpit itself has not reached 1.0 — before that
    /// milestone the project promises no compatibility at all (Versioning skill), so a <em>declared</em> "1.0.0 or
    /// later" on a 0.x host is the leftover template default every manifest used to carry regardless of what it
    /// actually needed, not a real requirement, and is not enforced. An <em>honest</em> sub-1.0 requirement (a
    /// plugin that says it needs 0.13.0) is a real, current claim and is enforced host 0.x or not — 21+ manifests
    /// already carry granular values in that range, each tied to a specific SDK member (see
    /// <c>Directory.Build.props</c>'s per-version changelog), so refusing a plugin that asks for a newer one than
    /// this host is exactly the case this gate exists to catch, not a false positive to suppress.
    /// </para>
    /// An unparsable or missing <paramref name="minHostVersion"/> is never a reason to refuse — a typo or an
    /// absent field means "nothing declared", not "unsupported".
    /// </summary>
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
