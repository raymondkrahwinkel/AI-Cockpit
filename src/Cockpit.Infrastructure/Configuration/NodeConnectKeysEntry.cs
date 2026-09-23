using System.Text.Json.Serialization;
using Cockpit.Core.Mcp;

namespace Cockpit.Infrastructure.Configuration;

// AC-1351: on-disk shape of the node's connect keys. Nothing here is a credential — a key is stored as its SHA-256
// and prefix — so it stays readable. `Policy` is absent until an operator writes one, and absent reads as the defaults.
internal sealed class NodeConnectKeysEntry
{
    public List<ConnectKey> Keys { get; set; } = [];

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ConnectKeyPolicy? Policy { get; set; }
}
