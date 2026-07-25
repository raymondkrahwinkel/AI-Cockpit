namespace Cockpit.Core.Mcp;

/// <summary>
/// The rough token estimate for a set of serialised MCP tool definitions (AC-134). A tool's cost is its name,
/// description and JSON schema as text, and text is counted at ~4 characters per token — the common
/// back-of-the-envelope ratio. It is model-agnostic (Claude, Qwen and Mistral all tokenise differently), so the
/// result is always presented as an estimate, never an exact budget.
/// </summary>
public static class McpToolTokenMath
{
    /// <summary>The chars-per-token ratio the estimate uses. ~4 is the widely-used rough English heuristic.</summary>
    public const double CharsPerToken = 4.0;

    /// <summary>Estimated tokens for the given serialised tool definitions: their total character length ÷ ~4, rounded up.</summary>
    public static int EstimateTokens(IEnumerable<string> serialisedTools)
    {
        var chars = serialisedTools.Sum(text => text?.Length ?? 0);
        return chars <= 0 ? 0 : (int)Math.Ceiling(chars / CharsPerToken);
    }

    /// <summary>A compact token count for a label: <c>850</c>, <c>1.2k</c>, <c>12k</c>. Invariant so the "k" and the decimal never shift with the UI culture.</summary>
    public static string Format(int tokens) =>
        tokens < 1000
            ? tokens.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : (tokens / 1000.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "k";
}
