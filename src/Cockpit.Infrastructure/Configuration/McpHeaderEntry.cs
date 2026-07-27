using Cockpit.Core.Mcp;

namespace Cockpit.Infrastructure.Configuration;

/// <summary>
/// On-disk shape of an <see cref="McpHeader"/> inside an <see cref="McpServerEntry"/>.
/// <para>
/// The value field is called <see cref="SecretValue"/> on purpose. <c>SecretFields</c> decides what to encrypt and
/// what to empty out of a backup by the <em>name</em> of the JSON field, so a field called <c>Value</c> would leave
/// a pasted token in plain sight and out of the scrubber's reach — the gap free-form rows fell into once before
/// (AC-295). Naming it this way puts every header value under the same protection as <see cref="McpServerEntry.ApiKey"/>,
/// which is the right default here: a custom header is a credential in all but name, and that is why the feature exists.
/// </para>
/// </summary>
internal sealed class McpHeaderEntry
{
    /// <summary>Nullable because a hand-edited config can write null here, and the deserializer assigns it.</summary>
    public string? Name { get; set; }

    /// <summary>Nullable for the same reason as <see cref="Name"/>.</summary>
    public string? SecretValue { get; set; }

    public static McpHeaderEntry FromDomain(McpHeader header) => new()
    {
        Name = header.Name,
        SecretValue = header.Value,
    };

    public McpHeader ToDomain() => new(Name ?? string.Empty, SecretValue ?? string.Empty);
}
