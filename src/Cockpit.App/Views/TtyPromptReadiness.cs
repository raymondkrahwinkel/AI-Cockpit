using System.Text.RegularExpressions;

namespace Cockpit.App.Views;

internal static partial class TtyPromptReadiness
{
    // AC-2164: native choice menus consume pasted turns; unknown UI keeps the existing startup fallback.
    public static bool IsBlocked(string screen) =>
        ChoiceLine().IsMatch(screen)
        || screen.Contains("Do you trust the files", StringComparison.OrdinalIgnoreCase)
        || screen.Contains("Do you trust the contents", StringComparison.OrdinalIgnoreCase)
        || screen.Contains("Yes, I trust this folder", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"^\s*[❯›>]\s*(?:\d+[.)]|[✔●])\s", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex ChoiceLine();
}
