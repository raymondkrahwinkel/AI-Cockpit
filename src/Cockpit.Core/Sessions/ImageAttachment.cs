namespace Cockpit.Core.Sessions;

/// <summary>
/// An image pasted/attached to a user message, carried alongside the text so the CLI receives it
/// as a stream-json <c>image</c> content block. Verified against claude.exe 2.1.197: the content
/// array accepts <c>{"type":"image","source":{"type":"base64","media_type":"image/png","data":"..."}}</c>.
/// </summary>
/// <param name="MediaType">The image MIME type, e.g. <c>image/png</c>.</param>
/// <param name="Base64Data">The raw image bytes, base64-encoded (no data-URI prefix).</param>
public sealed record ImageAttachment(string MediaType, string Base64Data)
{
    /// <summary>Builds an attachment from raw image bytes, base64-encoding them for the wire.</summary>
    public static ImageAttachment FromBytes(byte[] bytes, string mediaType) =>
        new(mediaType, Convert.ToBase64String(bytes));
}
