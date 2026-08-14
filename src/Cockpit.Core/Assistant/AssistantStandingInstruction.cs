namespace Cockpit.Core.Assistant;

// Composes the instruction an assistant session starts under: `AssistantSystemPrompt.Default` and
// whatever the operator wrote on the Assistant Profile (AC-594).
// The box on that dialog used to *replace* the default. It reads as a place to add a name or a house rule,
// and doing so silently dropped the language rule, the speak-don't-write rule, the honesty clause and the whole
// permission paragraph — none of which the operator was asking to lose. Adding is now the default and replacing is
// the advanced choice, which is also what the same field means on an ordinary profile.
public static class AssistantStandingInstruction
{
    // What the remembered lines are introduced as, so the assistant can tell them from its own instructions.
    public const string MemoryHeading =
        "What you have been asked to remember, from earlier conversations with this operator:";

    // What the state left behind at a hand-over is introduced as (AC-596).
    public const string CurrentStateHeading =
        "Where the conversation stood when you last handed over. It is yours, written before a restart, and it may "
        + "be out of date — treat it as a note to yourself rather than as something the operator just said:";

    // The instruction a session starts under: the built-in one (or the operator's, if they replaced it), then
    // whatever they wrote, then what was remembered (AC-595) and where the conversation stood (AC-596).
    //
    // `sdkAsksPermission`/`consentCardAsks` (AC-759): the two facts the acting paragraph's two gates need, read by
    // the caller off the Assistant Profile and the consent-bypass settings respectively and handed in rather than
    // looked up here — this type stays a pure formatter, and the two default to "still asks", the safest reading
    // when a caller has not looked (there is exactly one today: `AssistantSessionHost`, which always has). That
    // default is also what makes the common no-gate-info call sites (most of this file's own tests) keep returning
    // `AssistantSystemPrompt.Default` byte for byte.
    public static string Compose(
        string? operatorInstruction,
        bool replacesDefault,
        string? memory,
        string? currentState = null,
        bool sdkAsksPermission = true,
        bool consentCardAsks = true)
    {
        // `Default` already *is* `ActingParagraph(true, true)` in place, so the common case — nothing to swap in —
        // costs no allocation beyond `Default` itself; only a call that actually needs the less-cautious wording
        // pays for the substitution.
        var baseInstruction = sdkAsksPermission && consentCardAsks
            ? AssistantSystemPrompt.Default
            : AssistantSystemPrompt.Default.Replace(
                AssistantSystemPrompt.ActingParagraph(true, true),
                AssistantSystemPrompt.ActingParagraph(sdkAsksPermission, consentCardAsks),
                StringComparison.Ordinal);

        var written = operatorInstruction?.Trim();
        var instruction = string.IsNullOrEmpty(written)
            ? baseInstruction
            : replacesDefault ? written : baseInstruction + "\n\n" + written;

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
