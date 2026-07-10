namespace Cockpit.Core.Plugins;

/// <summary>Outcome of fetching a store's index (#14): the parsed catalogue and the resolved index URL (needed to resolve version zip paths), or a reason it failed.</summary>
public sealed record PluginStoreFetchResult(bool IsSuccess, string? Error, PluginStoreIndex? Index, string? IndexUrl);

/// <summary>Outcome of downloading a store plugin's zip (#14): the temp file path on success (checksum-verified when the index supplied one), or a reason it failed.</summary>
public sealed record PluginStoreDownloadResult(bool IsSuccess, string? Error, string? ZipPath);
