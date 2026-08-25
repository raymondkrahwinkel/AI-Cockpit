namespace Cockpit.Infrastructure.Formatting;

// Cutting text to a length without cutting through a character. One implementation, since it's used in more
// than one place (audit trails, the agent roster) and a second copy is a second chance to get it wrong.
internal static class BoundedText
{
    // Trims `text` to `maxLength` characters plus an ellipsis, or returns it unchanged if it already fits.
    // Surrogate-safe (C5): an astral character straddling the limit is not cut through, which would
    // otherwise leave a lone surrogate persisted or serialised as U+FFFD.
    internal static string Trim(string text, int maxLength)
    {
        if (text.Length <= maxLength)
        {
            return text;
        }

        var cut = char.IsHighSurrogate(text[maxLength - 1]) ? maxLength - 1 : maxLength;
        return text[..cut] + "…";
    }
}
