namespace Cockpit.Plugins.Abstractions;

/// <summary>
/// An image the operator sent with a user message (AC-14), offered to plugins that want to do something with it —
/// a tracker plugin attaches it to the issue the session is tracking. Provider-agnostic: the host does not know or
/// care what a sink does with it.
/// </summary>
/// <param name="MediaType">The image's MIME type, e.g. <c>image/png</c>.</param>
/// <param name="Base64Data">The image bytes, base64-encoded.</param>
/// <param name="SuggestedFileName">A file name a sink can use when it stores the image (the operator's paste carries none).</param>
public sealed record SessionImageAttachment(string MediaType, string Base64Data, string SuggestedFileName);
