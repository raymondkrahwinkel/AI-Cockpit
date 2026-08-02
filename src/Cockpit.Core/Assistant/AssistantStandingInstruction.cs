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
    public static string Compose(string? operatorInstruction, bool replacesDefault)
    {
        var written = operatorInstruction?.Trim();
        if (string.IsNullOrEmpty(written))
        {
            return AssistantSystemPrompt.Default;
        }

        return replacesDefault ? written : AssistantSystemPrompt.Default + "\n\n" + written;
    }
}
