using System.Text.Json.Serialization;
using Cockpit.Core.Swarm;

namespace Cockpit.Infrastructure.Configuration;

// AC-1916: on-disk shape of `SwarmSettings`. Null reads back as the empty default.
internal sealed class SwarmSettingsEntry
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? ExtraInternalCidrs { get; set; }

    public static SwarmSettingsEntry FromDomain(SwarmSettings settings) => new()
    {
        ExtraInternalCidrs = settings.ExtraInternalCidrs.Count == 0 ? null : [.. settings.ExtraInternalCidrs],
    };

    public SwarmSettings ToDomain() => new()
    {
        ExtraInternalCidrs = ExtraInternalCidrs ?? [],
    };
}
