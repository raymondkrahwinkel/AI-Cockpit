using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cockpit.Core.Profiles;

// One context value for the local model runtime and Cockpit's own turn guard. Invalid legacy values fall back to
// the profile default rather than disabling either boundary.
public static class LocalModelContext
{
    public const int DefaultTokens = 32_768;

    // AC-2421: a local model does not compact, so past this share of the window the next message starts fresh.
    public const int DefaultRestartPercent = 55;

    private const string _RestartKey = "restartAtPercent";

    // The profile's own line out of its `turnLimits`, or the default; 0 means never.
    public static int RestartPercent(JsonElement? turnLimits) =>
        turnLimits is { ValueKind: JsonValueKind.Object } limits
        && limits.EnumerateObject().FirstOrDefault(property => property.Name.Equals(_RestartKey, StringComparison.OrdinalIgnoreCase)).Value is { ValueKind: JsonValueKind.Number } value
        && value.TryGetInt32(out var percent)
            ? percent
            : DefaultRestartPercent;

    // `turnLimits` with this line in it and every other bound untouched; the default is stored as absence.
    public static JsonElement? WithRestartPercent(JsonElement? turnLimits, int percent)
    {
        if (RestartPercent(turnLimits) == percent)
        {
            return turnLimits;
        }

        var limits = (turnLimits is { ValueKind: JsonValueKind.Object } current ? JsonObject.Create(current) : null) ?? new JsonObject();
        foreach (var name in limits.Select(property => property.Key).Where(name => name.Equals(_RestartKey, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            limits.Remove(name);
        }

        if (percent != DefaultRestartPercent)
        {
            limits[_RestartKey] = percent;
        }

        return limits.Count == 0 ? null : JsonSerializer.SerializeToElement(limits);
    }

    public static int Normalize(int tokens) => tokens > 0 ? tokens : DefaultTokens;
}
