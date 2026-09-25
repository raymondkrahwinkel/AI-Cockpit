namespace Cockpit.Plugin.Diagram.Collab;

// AC-910: folds a line break out of document-derived text before it goes into an Ask message — same fold
// WireframeMcpTools._SingleLine applies to a Dangerous prompt, pulled out so AskMessage shares it.
internal static class SingleLineText
{
    public static string Fold(string value) =>
        new(value.Select(character =>
            char.IsControl(character) || character == 0x2028 || character == 0x2029 || character == 0x0085
                ? ' '
                : character).ToArray());
}
