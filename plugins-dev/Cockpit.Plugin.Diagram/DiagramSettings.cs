using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Diagram;

// AC-948: one skip-consent flag per surface, read fresh from `IPluginStorage` on every access so a settings save
// takes effect without a restart. Same shape as LocalCiSettings.SkipConsent, one per consent label instead of one
// for the whole plugin — a diagram opt-out must not silently also opt the whiteboard or wireframe out.
internal sealed class DiagramSettings(IPluginStorage storage)
{
    // AC-710 precedent: off by default, so a fresh install still asks every time.
    public bool SkipDiagramConsent
    {
        get => storage.Get<bool?>("skipDiagramConsent") ?? false;
        set => storage.Set("skipDiagramConsent", value);
    }

    public bool SkipWhiteboardConsent
    {
        get => storage.Get<bool?>("skipWhiteboardConsent") ?? false;
        set => storage.Set("skipWhiteboardConsent", value);
    }

    public bool SkipWireframeConsent
    {
        get => storage.Get<bool?>("skipWireframeConsent") ?? false;
        set => storage.Set("skipWireframeConsent", value);
    }
}
