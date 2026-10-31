using System.Text.RegularExpressions;

namespace Cockpit.Plugin.CliAgentProvider;

internal static class CliLoginText
{
    private static readonly Regex EscapeSequence = new(
        @"\x1b(?:\[[0-?]*[ -/]*[@-~]|\][^\x07\x1b]*(?:\x07|\x1b\\))",
        RegexOptions.Compiled);

    public static string StripEscapes(string text) => EscapeSequence.Replace(text, "");
}
