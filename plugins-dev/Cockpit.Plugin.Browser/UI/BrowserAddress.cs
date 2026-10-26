namespace Cockpit.Plugin.Browser.UI;

// What the address bar loads for what was typed: a web address as is, a bare host over https, anything else as a
// search. Other schemes (javascript:, file:, chrome:) are searched for, never loaded.
internal static class BrowserAddress
{
    private const string Search = "https://duckduckgo.com/?q=";

    public static string Resolve(string input)
    {
        var text = input.Trim();
        if (text.Length == 0)
        {
            return BrowserModel.StartAddress;
        }

        if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            return uri.AbsoluteUri;
        }

        if (text == BrowserModel.StartAddress)
        {
            return text;
        }

        var looksLikeHost = text.Contains(' ') == false
            && (text.Contains('.') || text.StartsWith("localhost", StringComparison.OrdinalIgnoreCase))
            && (text.Contains(':') == false || _HasPort(text));
        if (looksLikeHost && Uri.TryCreate("https://" + text, UriKind.Absolute, out var host))
        {
            return host.AbsoluteUri;
        }

        return Search + Uri.EscapeDataString(text);
    }

    // "localhost:5000" or "example.com:8080/path": a host with a port, not a scheme.
    private static bool _HasPort(string text)
    {
        var colon = text.IndexOf(':');
        var rest = text[(colon + 1)..];
        var digits = rest.TakeWhile(char.IsAsciiDigit).Count();
        return digits > 0 && (digits == rest.Length || rest[digits] == '/');
    }
}
