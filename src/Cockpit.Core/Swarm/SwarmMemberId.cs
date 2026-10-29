using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cockpit.Core.Swarm;

// AC-1915: a swarm member's id, the SHA-256 of its public key (SubjectPublicKeyInfo DER) in uppercase hex. Taken
// over the key, not the certificate, so a certificate renewed around the same key keeps the same member.
[JsonConverter(typeof(Converter))]
public sealed record SwarmMemberId
{
    private SwarmMemberId(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static SwarmMemberId FromPublicKey(ReadOnlySpan<byte> subjectPublicKeyInfo) =>
        new(Convert.ToHexString(SHA256.HashData(subjectPublicKeyInfo)));

    // Hex is case-blind, so either spelling parses to the one uppercase id.
    public static bool TryParse(string? text, [NotNullWhen(true)] out SwarmMemberId? id)
    {
        id = text is { Length: SHA256.HashSizeInBytes * 2 } && text.All(char.IsAsciiHexDigit)
            ? new SwarmMemberId(text.ToUpperInvariant())
            : null;
        return id is not null;
    }

    public override string ToString() => Value;

    internal sealed class Converter : JsonConverter<SwarmMemberId>
    {
        public override SwarmMemberId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            TryParse(reader.GetString(), out var id) ? id : throw new JsonException("A swarm member id is 64 hex characters.");

        public override void Write(Utf8JsonWriter writer, SwarmMemberId value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
