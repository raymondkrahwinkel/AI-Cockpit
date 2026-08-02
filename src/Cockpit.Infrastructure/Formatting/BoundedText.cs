namespace Cockpit.Infrastructure.Formatting;

// Cutting text to a length without cutting through a character. One implementation, because the rule is subtle enough
// that a second copy is a second chance to get it wrong: the cockpit bounds free text in more than one place — the
// audit trails before they write a line (`Auditing.JsonlAuditLog{T}`), the agent roster before it repeats
// one session's name and statusline into another's context (`AgentsMcpTools`) — and all of them want the same
// answer to "what does the last character do at the boundary".
internal static class BoundedText
{
    // Trims `text` to `maxLength` characters plus an ellipsis, or returns it
    // unchanged when it already fits. Surrogate-safe (C5): an astral character — an emoji in a command, say —
    // straddling the limit is not cut through, which would otherwise leave a lone surrogate that is persisted, or
    // serialised, as U+FFFD.
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
