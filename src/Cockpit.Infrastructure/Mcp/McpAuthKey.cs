using System.Security.Cryptography;
using System.Text;
using Cockpit.Core.Abstractions;

namespace Cockpit.Infrastructure.Mcp;

/// <summary>
/// The app-lifetime bearer key that guards the cockpit's own loopback MCP endpoints (AC-40). The endpoints listen on
/// an OS-assigned loopback port with no auth of their own, so any local process could enumerate the port and POST to
/// them; this key is the capability the endpoint checks. A fresh, cryptographically-random key is minted once per app
/// launch and never persisted — a key left over from a previous run is invalid after a restart.
/// <para>
/// The in-memory value here is the source of truth for validating incoming requests; the spawn paths hand the same
/// value to each in-app session so a session the operator started reaches the endpoints transparently, while a
/// process outside the app has no key and is turned away with a 401. The remaining boundary is honest and unchanged:
/// during a session the key sits in that session's owner-only mcp-config, readable by another same-user process that
/// knows the path — the same trust the rest of the config already rests on.
/// </para>
/// </summary>
internal sealed class McpAuthKey : ISingletonService
{
    /// <summary>The current run's key: 256 bits of randomness as hex, minted at construction and constant for the app's lifetime.</summary>
    public string Value { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    /// <summary>
    /// Whether an <c>Authorization</c> header carries this run's key. Compared in constant time so the check cannot
    /// leak the key a character at a time; a missing or malformed header is simply unauthorized.
    /// </summary>
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
