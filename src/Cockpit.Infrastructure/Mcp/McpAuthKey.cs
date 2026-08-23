using System.Security.Cryptography;
using System.Text;
using Cockpit.Core.Abstractions;

namespace Cockpit.Infrastructure.Mcp;

// AC-40: app-lifetime bearer key guarding the cockpit's loopback MCP endpoints, which otherwise have no auth of
// their own; minted fresh and unpersisted per launch, and handed only to in-app sessions so an outside process is
// turned away with a 401.
internal sealed class McpAuthKey : ISingletonService
{
    // The current run's key: 256 bits of randomness as hex, minted at construction and constant for the app's lifetime.
    public string Value { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    // Whether an `Authorization` header carries this run's key. Compared in constant time so the check cannot
    // leak the key a character at a time; a missing or malformed header is simply unauthorized.
    public bool IsAuthorized(string? authorizationHeader)
    {
        if (string.IsNullOrEmpty(authorizationHeader))
        {
            return false;
        }

        var provided = Encoding.UTF8.GetBytes(authorizationHeader);
        var expected = Encoding.UTF8.GetBytes($"Bearer {Value}");
        return CryptographicOperations.FixedTimeEquals(provided, expected);
    }
}
