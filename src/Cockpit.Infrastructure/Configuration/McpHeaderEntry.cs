using Cockpit.Core.Mcp;

namespace Cockpit.Infrastructure.Configuration;

// AC-295: on-disk shape of an `McpHeader`. The value field is named `SecretValue` on purpose —
// `SecretFields` matches by JSON field name, so a plain `Value` would leave a pasted token unscrubbed.
// A custom header is a credential in all but name, so it gets the same protection as `ApiKey`.
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
