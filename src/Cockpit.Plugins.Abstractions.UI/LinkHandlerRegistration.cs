namespace Cockpit.Plugins.Abstractions.UI;

[Flags]
public enum LinkClickModifiers
{
    None = 0,
    Control = 1,
    Shift = 2,
    Alt = 4,
    Meta = 8,
}

public delegate bool LinkMatcher(string text, out int start, out int length);

public sealed record LinkHandlerRegistration(
    LinkMatcher TryMatch,
    Func<string, LinkClickModifiers, bool> Handle);
