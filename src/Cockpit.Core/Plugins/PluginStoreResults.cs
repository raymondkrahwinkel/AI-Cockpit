namespace Cockpit.Core.Plugins;

/// <summary>Outcome of fetching a store's index (#14): the parsed catalogue and the resolved index URL (needed to resolve version zip paths), or a reason it failed.</summary>
public sealed record PluginStoreFetchResult(bool IsSuccess, string? Error, PluginStoreIndex? Index, string? IndexUrl);

/// <summary>Outcome of downloading a store plugin's zip (#14): the temp file path on success (checksum-verified when the index supplied one), or a reason it failed.</summary>
public sealed record PluginStoreDownloadResult(bool IsSuccess, string? Error, string? ZipPath);

/// <summary>The result of fetching a template's flow: the JSON itself, or why it could not be had.</summary>
public sealed record WorkflowTemplateDownloadResult(bool IsSuccess, string? Error, string? Json);

/// <summary>Outcome of fetching a store's logo image (#62): the raw image bytes on success (an http(s) image within the size cap), or a reason it failed — a broken logo falls back to the store's emoji/default glyph and never blocks browsing.</summary>
public sealed record PluginStoreImageResult(bool IsSuccess, string? Error, byte[]? Bytes);
