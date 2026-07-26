using System.Globalization;
using System.Text.RegularExpressions;

namespace Cockpit.Plugin.KimiProvider;

/// <summary>
/// Extracts the context-used percentage from the free text kimi's <c>/usage</c> (and <c>/status</c>) builtin
/// commands answer with (protocol §11) — the only wire-level source of token/context data ACP exposes, since
/// neither <c>PromptResponse</c> nor any <c>session/update</c> variant carries a usage field. Pure, static,
/// stateless; an unexpected shape returns <see langword="null"/> rather than guessing (AC-274).
/// </summary>
internal static class KimiUsageParser
{
    // Matches the one line both /usage and /status end with: "Context: 45,000 / 200,000 (22.5%)". The two token
    // counts are toLocaleString('en-US')-formatted (thousands-comma groups of exactly three digits); the
    // percentage is (contextUsage*100).toFixed(1), so always at least one fractional digit and never clamped to
    // 100. Everything else on the line (Total/Current turn/per-model rows) is ignored on purpose.
    private static readonly Regex _ContextLineRegex = new(
        @"Context:\s*(\d{1,3}(?:,\d{3})*)\s*/\s*(\d{1,3}(?:,\d{3})*)\s*\((\d+\.\d+)%\)",
        RegexOptions.Compiled);

    public static double? ParseContextUsedPercent(string? usageOrStatusText)
    {
        if (string.IsNullOrWhiteSpace(usageOrStatusText))
        {
            return null;
        }

        var match = _ContextLineRegex.Match(usageOrStatusText);
        if (!match.Success)
        {
            return null;
        }

        // The two token counts are otherwise unused (only the percentage is reported), but parsing them here
        // with the commas stripped and invariant culture is the validation: a count that does not survive this
        // round-trip means the line drifted from protocol §11, and the percentage capture is not trusted either
        // ("never guess at a number" — AC-274 literal acceptance criterion).
        if (!_TryParseThousandsGroupedInteger(match.Groups[1].Value) || !_TryParseThousandsGroupedInteger(match.Groups[2].Value))
        {
            return null;
        }

        return double.TryParse(match.Groups[3].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var percent)
            ? percent
            : null;
    }

    private static bool _TryParseThousandsGroupedInteger(string value) =>
        long.TryParse(value.Replace(",", string.Empty), NumberStyles.None, CultureInfo.InvariantCulture, out _);
}
