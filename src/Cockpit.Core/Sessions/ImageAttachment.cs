namespace Cockpit.Core.Sessions;

// An image pasted/attached to a user message, carried alongside the text so the CLI receives it
// as a stream-json `image` content block. Verified against claude.exe 2.1.197: the content
// array accepts `{"type":"image","source":{"type":"base64","media_type":"image/png","data":"..."}}`.
//
// `MediaType`: The image MIME type, e.g. `image/png`.
// `Base64Data`: The raw image bytes, base64-encoded (no data-URI prefix).
public sealed record ImageAttachment(string MediaType, string Base64Data)
{
    // Builds an attachment from raw image bytes, base64-encoding them for the wire.
    public static ImageAttachment FromBytes(byte[] bytes, string mediaType) =>
        new(mediaType, Convert.ToBase64String(bytes));
}
