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

    /// <summary>What the state left behind at a hand-over is introduced as (AC-596).</summary>
    public const string CurrentStateHeading =
        "Where the conversation stood when you last handed over. It is yours, written before a restart, and it may "
        + "be out of date — treat it as a note to yourself rather than as something the operator just said:";

    /// <summary>
    /// The instruction a session starts under: the built-in one (or the operator's, if they replaced it), then
    /// whatever they wrote, then what was remembered (AC-595) and where the conversation stood (AC-596).
    /// </summary>
    public static string Compose(
        string? operatorInstruction,
        bool replacesDefault,
        string? memory,
        string? currentState = null)
    {
        var written = operatorInstruction?.Trim();
        var instruction = string.IsNullOrEmpty(written)
            ? AssistantSystemPrompt.Default
            : replacesDefault ? written : AssistantSystemPrompt.Default + "\n\n" + written;

        // Last, and each under a heading of its own: this is the operator's material and the assistant's own note
        // rather than the product's rules, and one that cannot tell them apart recites a remembered line as a rule.
        return _Append(_Append(instruction, MemoryHeading, memory), CurrentStateHeading, currentState);
    }

    private static string _Append(string instruction, string heading, string? block)
    {
        var text = block?.Trim();
        return string.IsNullOrEmpty(text) ? instruction : instruction + "\n\n" + heading + "\n\n" + text;
    }
}
