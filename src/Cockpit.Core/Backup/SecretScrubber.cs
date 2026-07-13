using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cockpit.Core.Backup;

/// <summary>
/// Takes the secrets out of the settings before they go into an archive (#70). A backup without credentials is the
/// default, and this is what makes that claim true rather than merely intended.
/// <para>
/// It works by name, not by value: a field called <c>token</c>, <c>apiKey</c>, <c>webhook</c>, <c>secret</c> or
/// <c>password</c> is emptied wherever it sits. Names, not values, because a value that merely <em>looks</em> like a
/// key is a guess, and a guess in either direction is a mistake you cannot see — one leaks, the other quietly wipes
/// something you needed.
/// </para>
/// <para>
/// It also goes <em>into</em> the plugin storage. A plugin keeps its settings as a JSON string inside the cockpit's
/// own JSON — which is where the YouTrack token actually lives. A scrubber that only walked the outer document would
/// report a clean backup and ship the token.
/// </para>
/// </summary>
public static class SecretScrubber
{
    private static readonly string[] Secretish =
    [
        "token",
        "apikey",
        "api_key",
        "secret",
        "password",
        "webhook",
    ];

    /// <summary>Empties every secret-looking field in <paramref name="settings"/>, in place, and returns the paths it emptied — which is what the restore tells the operator they must type in again.</summary>
    public static IReadOnlyList<string> Scrub(JsonNode settings)
    {
        var removed = new List<string>();
        _Walk(settings, string.Empty, removed);

        return removed;
    }

    /// <summary>Whether a field's name says it holds a credential.</summary>
    public static bool IsSecret(string name) =>
        Secretish.Any(hint => name.Contains(hint, StringComparison.OrdinalIgnoreCase));

    private static void _Walk(JsonNode? node, string path, List<string> removed)
    {
        switch (node)
        {
            case JsonObject json:
                foreach (var (key, value) in json.ToList())
                {
                    var here = path.Length == 0 ? key : $"{path}.{key}";

                    if (IsSecret(key) && value is JsonValue)
                    {
                        if (value.GetValue<object>()?.ToString() is { Length: > 0 })
                        {
                            json[key] = string.Empty;
                            removed.Add(here);
                        }

                        continue;
                    }

                    _Walk(value, here, removed);

                    // A plugin's storage is JSON inside a string. Rewritten only when something was actually taken
                    // out of it, so a backup does not gratuitously reformat every plugin's settings.
                    if (value is JsonValue text && text.TryGetValue<string>(out var raw) && _Embedded(raw) is { } embedded)
                    {
                        var before = removed.Count;
                        _Walk(embedded, here, removed);

                        if (removed.Count > before)
                        {
                            json[key] = embedded.ToJsonString();
                        }
                    }
                }

                break;

            case JsonArray array:
                for (var index = 0; index < array.Count; index++)
                {
                    _Walk(array[index], $"{path}[{index}]", removed);
                }

                break;
        }
    }

    // A string that is itself a JSON object or array — how a plugin stores its settings inside the cockpit's.
    private static JsonNode? _Embedded(string value)
    {
        var trimmed = value.TrimStart();
        if (trimmed.Length == 0 || (trimmed[0] != '{' && trimmed[0] != '['))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(value);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
