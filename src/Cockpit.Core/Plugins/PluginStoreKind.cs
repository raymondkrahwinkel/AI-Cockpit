namespace Cockpit.Core.Plugins;

// Where a configured plugin store lives (AC-7): a remote http(s) index, or a folder on disk.
public enum PluginStoreKind
{
    // An http(s) store — a public one, or a private one reached with a bearer token.
    Remote,

    // A folder on this machine holding an `index.json` and the zips it lists.
    Local,
}
