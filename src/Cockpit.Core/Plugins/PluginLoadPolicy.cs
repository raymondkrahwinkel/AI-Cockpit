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
        //
        // So the gate only bites from 1.0.0 onwards. Enforcing it against a 0.x host would refuse every plugin in
        // existence today — including the ones this build ships with — over a number nobody meant. Before 1.0 the
        // cockpit promises no compatibility anyway, which is exactly the window in which the manifests can be made
        // honest; after it, this refuses rather than loading something that will break out of sight.
        if (hostVersion is { Major: >= 1 }
            && Version.TryParse(manifest.MinHostVersion, out var required)
            && hostVersion < required)
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
}
