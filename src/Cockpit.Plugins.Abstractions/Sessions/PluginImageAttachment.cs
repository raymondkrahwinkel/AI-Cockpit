namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>
/// An image pasted/attached to a user message, carried alongside the text so a provider that supports vision (#64)
/// receives it as an image content block — the plugin-facing mirror of <c>Cockpit.Core.Sessions.ImageAttachment</c>.
/// The host's driver adapter converts one to the other at the plugin boundary, so this assembly never references
/// <c>Cockpit.Core</c>.
/// </summary>
/// <param name="MediaType">The image MIME type, e.g. <c>image/png</c>.</param>
/// <param name="Base64Data">The raw image bytes, base64-encoded (no data-URI prefix).</param>
public sealed record PluginImageAttachment(string MediaType, string Base64Data);
