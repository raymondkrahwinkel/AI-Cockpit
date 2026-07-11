namespace Cockpit.App.Services;

/// <summary>One installed plugin with a newer version advertised by a configured store (#59).</summary>
internal sealed record PluginUpdateInfo(string FolderId, string Name, string InstalledVersion, string LatestVersion);
