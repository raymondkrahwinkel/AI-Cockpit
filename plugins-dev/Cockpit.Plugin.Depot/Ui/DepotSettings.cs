using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Depot.UI;

// The plugin's settings, persisted through the host's per-plugin `IPluginStorage` (AC-243) — the client-local
// (not synced) half of AC-242. Nothing here is secret (see `DepotConnectionRegistration`), so this is plain JSON
// metadata; the credential the host acquires for each contributed server lives in the host's own OAuth store,
// never in this plugin's storage. Read fresh on every access, so a settings save takes effect on the next call
// without a restart.
//
// The UI part's own copy of the backend part's identically-named class under Settings/ (AC-1394) — both read the
// same storage slice through IPluginStorage, so a save through the backend part's own copy (over the plugin's
// channel) is visible here on the very next read. See Contracts/DepotChannel.cs's own remarks for why this is a
// separate copy rather than a linked one. The AC-499 stored-URL migration lives only in the backend part's copy —
// the backend part's Initialize always runs before this settings view can be opened, so by the time this copy
// ever reads "connections", the migration has already run once.
internal sealed class DepotSettings(IPluginStorage storage)
{
    // "Open files in" (AC-1781): the preview tab, as VS Code does, or only a selection with the detail strip below the tree.
    public bool OpenFilesInPreviewTab
    {
        get => storage.Get<string>("openFilesIn") != "below";
        set => storage.Set("openFilesIn", value ? "preview" : "below");
    }

    public IReadOnlyList<DepotConnectionRegistration> Connections
    {
        get => storage.Get<List<DepotConnectionRegistration>>("connections") ?? [];
        set => storage.Set("connections", value.ToList());
    }
}
