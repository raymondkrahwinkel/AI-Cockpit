namespace Cockpit.Core.Sessions;

// An image pasted/attached to a user message, sent as a stream-json `image` content block (verified
// against claude.exe 2.1.197). `MediaType` is the MIME type; `Base64Data` is the raw bytes, base64-encoded.
public sealed record ImageAttachment(string MediaType, string Base64Data)
{
    // Builds an attachment from raw image bytes, base64-encoding them for the wire.
    public static ImageAttachment FromBytes(byte[] bytes, string mediaType) =>
        new(mediaType, Convert.ToBase64String(bytes));
}
