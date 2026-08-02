namespace Cockpit.Core.Projects;

// Splits a project's `Project.MemoryRef` into a scheme and its value (AC-165/166) — the one rule shared
// between the session's standing instructions (`Sessions.SessionStartDefaults`) and the project editor's
// picker, kept in one place so the two parse a reference identically rather than agreeing on the rule by accident.
public static class ProjectMemoryRef
{
    // True when `memoryRef` has the shape `&lt;scheme&gt;:&lt;value&gt;` with a scheme of at
    // least two characters and a value that is not itself blank, in which case `scheme` and
    // `value` (trimmed) are set. False for anything else — a plain path, a reference with nothing
    // after the colon — leaving both out parameters empty.
    //
    // ⚠️ The two-character floor is not cosmetic: a Windows path (`C:\Users\raymond`) puts a colon at index 1
    // too, with "C" in front of it. Without this floor, a source that registered the single-character scheme "c"
    // would parse every such path as a reference to it instead of the folder it plainly is.
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

    // True when `scheme` is one `TryParse` can ever hand back — the one rule a memory
    // source's registration must satisfy, kept here rather than duplicated by whichever registry accepts one, so a
    // source the picker offers is always one a starting session can actually recognise later, rather than one that
    // shows up in the dialog and then falls silent.
    //
    // Four conditions, each closing a different gap a looser check would leave open:
    //
    // ⚠️ Not blank — the same reason a blank `Title` or `Instruction` is refused: nothing to key a
    // reference on or to match one against.
    //
    // ⚠️ At least two characters — the same floor `TryParse` enforces on the text before the colon (see
    // its own remark on the Windows-path collision that floor exists to avoid). A scheme shorter than that is one
    // `TryParse` would never split out of a stored reference in the first place, so registering it
    // would offer a choice the parser can never match back.
    //
    // ⚠️ Contains no colon — `TryParse` splits a reference on its *first* colon, so the scheme it
    // hands back is always the text before that split. A scheme containing one of its own could therefore never be
    // the text `TryParse` extracts, whatever reference stored it.
    //
    // ⚠️ Equal to its own `string.Trim()` — a starting session trims the stored reference before
    // parsing it (`Cockpit.Core.Sessions.SessionStartDefaults`), while the project editor parses the
    // reference it saved as-is. A scheme with surrounding whitespace can therefore match in one of those two places
    // and not the other for the very same stored reference — the picker offers it and the session falls back to the
    // unexplained sentence, or the reverse — which is exactly the "stood offered, then silent" failure this method
    // exists to keep out of the registry in the first place.
    public static bool IsUsableScheme(string? scheme) =>
        !string.IsNullOrWhiteSpace(scheme)
        && scheme.Length >= 2
        && !scheme.Contains(':')
        && scheme == scheme.Trim();
}
