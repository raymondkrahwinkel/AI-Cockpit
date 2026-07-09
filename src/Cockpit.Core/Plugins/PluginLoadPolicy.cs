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
        string currentSha256)
    {
        if (manifest.AbstractionsVersion != hostAbstractionsMajor)
        {
            return PluginLoadDecision.AbstractionsMajorMismatch;
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
