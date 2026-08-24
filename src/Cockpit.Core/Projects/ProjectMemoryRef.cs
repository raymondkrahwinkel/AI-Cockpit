namespace Cockpit.Core.Projects;

// Splits a project's `Project.MemoryRef` into a scheme and its value (AC-165/166) — the one rule shared
// between the session's standing instructions (`Sessions.SessionStartDefaults`) and the project editor's
// picker, kept in one place so the two parse a reference identically rather than agreeing on the rule by accident.
public static class ProjectMemoryRef
{
    // AC-1013: True when `memoryRef` is `<scheme>:<value>` with scheme >= 2 chars and non-blank value,
    // setting both trimmed out params; false otherwise. Two-char floor avoids a Windows path (`C:\...`)
    // being misread as scheme "c" — see IsUsableScheme's own remark on why that floor is enforced twice.
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

    // AC-1013: True when `scheme` is one TryParse can ever hand back. Four conditions: non-blank, >= 2 chars
    // (mirrors TryParse's floor), no colon (TryParse splits on the first one), equal to its own Trim()
    // (session trims before parsing, editor doesn't — mismatched whitespace would offer-then-fail-silently).
    public static bool IsUsableScheme(string? scheme) =>
        !string.IsNullOrWhiteSpace(scheme)
        && scheme.Length >= 2
        && !scheme.Contains(':')
        && scheme == scheme.Trim();
}
