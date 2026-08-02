using Cockpit.Core.Mcp;

namespace Cockpit.Infrastructure.Configuration;

// On-disk shape of an `McpHeader` inside an `McpServerEntry`.
//
// The value field is called `SecretValue` on purpose. `SecretFields` decides what to encrypt and
// what to empty out of a backup by the *name* of the JSON field, so a field called `Value` would leave
// a pasted token in plain sight and out of the scrubber's reach — the gap free-form rows fell into once before
// (AC-295). Naming it this way puts every header value under the same protection as `McpServerEntry.ApiKey`,
// which is the right default here: a custom header is a credential in all but name, and that is why the feature exists.
internal sealed class McpHeaderEntry
{
    // Nullable because a hand-edited config can write null here, and the deserializer assigns it.
    public string? Name { get; set; }

    // Nullable for the same reason as `Name`.
    public string? SecretValue { get; set; }

    public static McpHeaderEntry FromDomain(McpHeader header) => new()
    {
        Name = header.Name,
        SecretValue = header.Value,
    };

    public McpHeader ToDomain() => new(Name ?? string.Empty, SecretValue ?? string.Empty);
}
