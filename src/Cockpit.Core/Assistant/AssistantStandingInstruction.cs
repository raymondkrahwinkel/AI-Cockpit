namespace Cockpit.Core.Assistant;

/// <summary>
/// Composes the instruction an assistant session starts under: <see cref="AssistantSystemPrompt.Default"/> and
/// whatever the operator wrote on the Assistant Profile (AC-594).
/// </summary>
/// <remarks>
/// The box on that dialog used to <em>replace</em> the default. It reads as a place to add a name or a house rule,
/// and doing so silently dropped the language rule, the speak-don't-write rule, the honesty clause and the whole
/// permission paragraph — none of which the operator was asking to lose. Adding is now the default and replacing is
/// the advanced choice, which is also what the same field means on an ordinary profile.
/// </remarks>
public static class AssistantStandingInstruction
{
    /// <summary>What the remembered lines are introduced as, so the assistant can tell them from its own instructions.</summary>
    public const string MemoryHeading =
        "What you have been asked to remember, from earlier conversations with this operator:";

    /// <summary>
    /// The instruction a session starts under: the built-in one (or the operator's, if they replaced it), then
    /// whatever they wrote, then what the assistant was asked to remember (AC-595).
    /// </summary>
    public static string Compose(string? operatorInstruction, bool replacesDefault, string? memory = null)
    {
        var written = operatorInstruction?.Trim();
        var instruction = string.IsNullOrEmpty(written)
            ? AssistantSystemPrompt.Default
            : replacesDefault ? written : AssistantSystemPrompt.Default + "\n\n" + written;

        // Last, and under a heading of its own: it is the operator's material rather than the product's, and an
        // assistant that cannot tell the two apart would recite a remembered line as if it were a rule.
        var remembered = memory?.Trim();
        return string.IsNullOrEmpty(remembered)
            ? instruction
            : instruction + "\n\n" + MemoryHeading + "\n\n" + remembered;
    }
}
