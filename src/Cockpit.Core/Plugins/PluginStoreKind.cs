namespace Cockpit.Core.Plugins;

/// <summary>Where a configured plugin store lives (AC-7): a remote http(s) index, or a folder on disk.</summary>
public enum PluginStoreKind
{
    /// <summary>An http(s) store — a public one, or a private one reached with a bearer token.</summary>
    Remote,

    /// <summary>A folder on this machine holding an <c>index.json</c> and the zips it lists.</summary>
    Local,
}
