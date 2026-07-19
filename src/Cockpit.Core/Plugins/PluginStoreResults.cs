namespace Cockpit.Core.Plugins;

/// <summary>Outcome of fetching a store's index (#14): the parsed catalogue and the resolved index URL (needed to resolve version zip paths), or a reason it failed.</summary>
public sealed record PluginStoreFetchResult(bool IsSuccess, string? Error, PluginStoreIndex? Index, string? IndexUrl);

/// <summary>Outcome of downloading a store plugin's zip (#14): the temp file path on success (checksum-verified when the index supplied one), or a reason it failed. <paramref name="Warning"/> is a non-fatal advisory the caller should surface — e.g. the store published no checksum, so integrity could not be verified (AC-46).</summary>
public sealed record PluginStoreDownloadResult(bool IsSuccess, string? Error, string? ZipPath, string? Warning = null);

/// <summary>The result of fetching a template's flow: the JSON itself, or why it could not be had. <paramref name="Warning"/> carries a non-fatal advisory (e.g. an unverifiable download) the caller should surface (AC-46).</summary>
public sealed record WorkflowTemplateDownloadResult(bool IsSuccess, string? Error, string? Json, string? Warning = null);

/// <summary>Outcome of fetching a store's logo image (#62): the raw image bytes on success (an http(s) image within the size cap), or a reason it failed — a broken logo falls back to the store's emoji/default glyph and never blocks browsing.</summary>
public sealed record PluginStoreImageResult(bool IsSuccess, string? Error, byte[]? Bytes);
