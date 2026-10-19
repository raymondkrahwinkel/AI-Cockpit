using System.Text.Json;
using System.Text.Json.Serialization;
using Cockpit.Core.Profiles;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.BackendApi;

// AC-1585: a whole profile over the admin connection, in the shape cockpit.json stores it, so a field the profile gains
// later travels without a wire change of its own. Both ends of the connection read and write it through here.
internal static class RemoteProfileWire
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static JsonElement ToJson(SessionProfile profile) =>
        JsonSerializer.SerializeToElement(SessionProfileEntry.FromDomain(profile), Json);

    // Null when the JSON is not a profile.
    public static SessionProfile? FromJson(JsonElement json)
    {
        try
        {
            return json.ValueKind == JsonValueKind.Object ? json.Deserialize<SessionProfileEntry>(Json)?.ToDomain() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
