namespace Cockpit.App.ViewModels;

// The "[+N image(s)]" fragment a message with pasted images carries, shared by the queued-message chip
// (pre-send) and the transcript row's own image chip (AC-778, post-send) so the wording cannot drift between
// the two.
internal static class ImageCountLabel
{
    public static string Format(int count) =>
        count == 0 ? string.Empty : $"[+{count} image{(count == 1 ? "" : "s")}]";
}
