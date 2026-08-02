namespace Cockpit.App.Services;

// One installed plugin with a newer version advertised by a configured store (#59).
internal sealed record PluginUpdateInfo(string FolderId, string Name, string InstalledVersion, string LatestVersion);
