namespace Cockpit.Core.Projects;

/// <summary>
/// Splits a project's <see cref="Project.MemoryRef"/> into a scheme and its value (AC-165/166) — the one rule shared
/// between the session's standing instructions (<see cref="Sessions.SessionStartDefaults"/>) and the project editor's
/// picker, kept in one place so the two parse a reference identically rather than agreeing on the rule by accident.
/// </summary>
public static class ProjectMemoryRef
{
    /// <summary>
    /// True when <paramref name="memoryRef"/> has the shape <c>&lt;scheme&gt;:&lt;value&gt;</c> with a scheme of at
    /// least two characters and a value that is not itself blank, in which case <paramref name="scheme"/> and
    /// <paramref name="value"/> (trimmed) are set. False for anything else — a plain path, a reference with nothing
    /// after the colon — leaving both out parameters empty.
    /// <para>
    /// ⚠️ The two-character floor is not cosmetic: a Windows path (<c>C:\Users\raymond</c>) puts a colon at index 1
    /// too, with "C" in front of it. Without this floor, a source that registered the single-character scheme "c"
    /// would parse every such path as a reference to it instead of the folder it plainly is.
    /// </para>
    /// </summary>
    public static bool TryParse(string? memoryRef, out string scheme, out string value)
    {
        scheme = string.Empty;
        value = string.Empty;

        if (string.IsNullOrWhiteSpace(memoryRef))
        {
            return false;
        }

        var separator = memoryRef.IndexOf(':');
        if (separator < 2)
        {
            return false;
        }

        var candidateValue = memoryRef[(separator + 1)..].Trim();
        if (candidateValue.Length == 0)
        {
            return false;
        }

        scheme = memoryRef[..separator];
        value = candidateValue;
        return true;
    }

    /// <summary>
    /// True when <paramref name="scheme"/> is one <see cref="TryParse"/> can ever hand back — the one rule a memory
    /// source's registration must satisfy, kept here rather than duplicated by whichever registry accepts one, so a
    /// source the picker offers is always one a starting session can actually recognise later, rather than one that
    /// shows up in the dialog and then falls silent.
    /// <para>
    /// Four conditions, each closing a different gap a looser check would leave open:
    /// </para>
    /// <para>
    /// ⚠️ Not blank — the same reason a blank <c>Title</c> or <c>Instruction</c> is refused: nothing to key a
    /// reference on or to match one against.
    /// </para>
    /// <para>
    /// ⚠️ At least two characters — the same floor <see cref="TryParse"/> enforces on the text before the colon (see
    /// its own remark on the Windows-path collision that floor exists to avoid). A scheme shorter than that is one
    /// <see cref="TryParse"/> would never split out of a stored reference in the first place, so registering it
    /// would offer a choice the parser can never match back.
    /// </para>
    /// <para>
    /// ⚠️ Contains no colon — <see cref="TryParse"/> splits a reference on its <em>first</em> colon, so the scheme it
    /// hands back is always the text before that split. A scheme containing one of its own could therefore never be
    /// the text <see cref="TryParse"/> extracts, whatever reference stored it.
    /// </para>
    /// <para>
    /// ⚠️ Equal to its own <see cref="string.Trim()"/> — a starting session trims the stored reference before
    /// parsing it (<see cref="Cockpit.Core.Sessions.SessionStartDefaults"/>), while the project editor parses the
    /// reference it saved as-is. A scheme with surrounding whitespace can therefore match in one of those two places
    /// and not the other for the very same stored reference — the picker offers it and the session falls back to the
    /// unexplained sentence, or the reverse — which is exactly the "stood offered, then silent" failure this method
    /// exists to keep out of the registry in the first place.
    /// </para>
    /// </summary>
    public static bool IsUsableScheme(string? scheme) =>
        !string.IsNullOrWhiteSpace(scheme)
        && scheme.Length >= 2
        && !scheme.Contains(':')
        && scheme == scheme.Trim();
}
