using Cockpit.Core.Sessions;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Shared;

// AC-116: a turn's images as a plugin receives them, named in the order they were pasted. AC-1415: shared by the
// desktop pane and the backend's observer, so both name them alike.
internal static class SessionImageAttachments
{
    public static IReadOnlyList<SessionImageAttachment> From(IReadOnlyList<ImageAttachment> images) =>
        [.. images.Select((image, index) => new SessionImageAttachment(
            image.MediaType, image.Base64Data, $"pasted-image-{index + 1}.{_Extension(image.MediaType)}"))];

    private static string _Extension(string mediaType)
    {
        var subtype = mediaType.Split('/').LastOrDefault() ?? string.Empty;
        // A compound subtype (image/svg+xml) or one with parameters (…;charset=…) must not leak "+xml"/";…" into
        // the file name.
        var clean = subtype.Split('+', ';')[0].Trim();

        return clean.Length > 0 ? clean : "png";
    }
}
