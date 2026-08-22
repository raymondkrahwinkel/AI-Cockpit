namespace Cockpit.Core.Help;

// Where a deep link points: `article` on its own, or `article#section`. One spelling, used from the app,
// from a plugin and from a `help:` link inside the documentation itself, so there is a single thing to
// resolve and a single thing for the deep-link test to check.
public sealed record HelpAddress(string Article, string? Section = null)
{
    public static HelpAddress Parse(string address)
    {
        var text = (address ?? string.Empty).Trim();
        var hash = text.IndexOf('#');

        return hash < 0
            ? new HelpAddress(text, null)
            : new HelpAddress(text[..hash].Trim(), _Trimmed(text[(hash + 1)..]));
    }

    public override string ToString() => Section is null ? Article : $"{Article}#{Section}";

    private static string? _Trimmed(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }
}
