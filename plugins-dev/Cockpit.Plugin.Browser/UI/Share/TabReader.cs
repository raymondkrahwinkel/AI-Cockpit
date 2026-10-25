using System.Text;
using System.Text.Json;

namespace Cockpit.Plugin.Browser.UI.Share;

// What an agent may get out of a shared tab (AC-1714), and nothing else: the visible text, the accessibility tree or a
// screenshot. The read script touches no cookie, storage or form value, and each result keeps only the one field asked.
internal static class TabReader
{
    public const string TextScript = "document.body ? document.body.innerText : ''";

    // Enough for a long article; the rest is cut so one read cannot flood an agent's context.
    private const int MaxText = 200_000;

    public static IReadOnlyDictionary<string, object> TextParameters { get; } =
        new Dictionary<string, object> { ["expression"] = TextScript, ["returnByValue"] = true };

    public static IReadOnlyDictionary<string, object> ScreenshotParameters { get; } =
        new Dictionary<string, object> { ["format"] = "png" };

    // Runtime.evaluate's result: only result.value, as a string.
    public static string? Text(string? evaluateResult)
    {
        if (_Parse(evaluateResult) is not { } root
            || root.TryGetProperty("result", out var result) == false
            || result.TryGetProperty("value", out var value) == false
            || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetString() ?? string.Empty;
        return text.Length > MaxText ? text[..MaxText] : text;
    }

    // Accessibility.getFullAXTree's result as one "role: name" line per named node that is not ignored.
    public static string? Tree(string? axTreeResult)
    {
        if (_Parse(axTreeResult) is not { } root || root.TryGetProperty("nodes", out var nodes) == false || nodes.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var lines = new StringBuilder();
        foreach (var node in nodes.EnumerateArray())
        {
            if (node.TryGetProperty("ignored", out var ignored) && ignored.ValueKind == JsonValueKind.True)
            {
                continue;
            }

            var role = _Value(node, "role");
            var name = _Value(node, "name");
            if (string.IsNullOrWhiteSpace(name) == false && lines.Length < MaxText)
            {
                lines.Append(role).Append(": ").AppendLine(name);
            }
        }

        return lines.ToString();
    }

    // Page.captureScreenshot's result: the base64 PNG only.
    public static string? Png(string? captureResult) =>
        _Parse(captureResult) is { } root && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.String ? data.GetString() : null;

    private static string? _Value(JsonElement node, string property) =>
        node.TryGetProperty(property, out var holder) && holder.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static JsonElement? _Parse(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        try
        {
            return JsonDocument.Parse(json).RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
