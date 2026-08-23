namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>
/// One MCP server the host resolved from its shared registry for a session to expose to its provider (#44/#26).
/// The host does the resolving — it turns the operator's per-session selection into concrete endpoints — and a
/// driver that spawns an agent CLI (Codex) turns these into whatever config that CLI reads. A driver with no
/// tool source of its own simply ignores them.
/// </summary>
/// <remarks>
/// One of <see cref="Url"/> (an HTTP server) or <see cref="Command"/> (a stdio server) is set; the other is
/// <see langword="null"/>. <see cref="BearerToken"/> is a user API-key server's <em>own</em> credential — a secret,
/// so it must never be written where another local account can read it (a process argument, a world-readable file);
/// see the driver that consumes it for how it is kept off the command line. A cockpit-hosted loopback endpoint
/// (<see cref="CockpitHosted"/>) carries no literal token here: its auth is the app-lifetime key, which the host
/// puts in the <c>COCKPIT_MCP_KEY</c> environment variable and each driver references from there (AC-40), so that
/// key never lands in a config file at all.
/// </remarks>
public sealed record PluginMcpServer
{
    /// <summary>
    /// The server's registry name — also the key the provider registers it under.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Endpoint URL for an HTTP server; <see langword="null"/> for a stdio server.
    /// </summary>
    public string? Url { get; init; }

    /// <summary>
    /// Executable for a stdio server; <see langword="null"/> for an HTTP server.
    /// </summary>
    public string? Command { get; init; }

    /// <summary>
    /// Arguments for the stdio <see cref="Command"/>.
    /// </summary>
    public IReadOnlyList<string> Args { get; init; } = [];

    /// <summary>
    /// A user API-key server's own static bearer token, or <see langword="null"/> when the server needs none (a
    /// localhost server), negotiates its own auth (OAuth), or is a cockpit-hosted endpoint (<see cref="CockpitHosted"/>).
    /// Sent as <c>Authorization: Bearer</c>.
    /// </summary>
    public string? BearerToken { get; init; }

    /// <summary>
    /// Whether this is a loopback MCP endpoint the cockpit itself hosts (AC-40). Its auth is not a literal token but
    /// the app-lifetime key in the <c>COCKPIT_MCP_KEY</c> environment variable: a driver writing a config references
    /// that variable in the server's <c>Authorization</c> header (e.g. <c>Bearer ${COCKPIT_MCP_KEY}</c>) rather than
    /// embedding a secret, so nothing sensitive is written to disk. Never set for a user-added server.
    /// </summary>
    public bool CockpitHosted { get; init; }

    /// <summary>
    /// Extra headers the operator configured for this server, for one that wants <c>X-Api-Key</c> or another scheme
    /// rather than <c>Authorization: Bearer</c> (AC-354). Treat every value as a credential: it is where a token goes
    /// when the server does not take a bearer, so it must never be written where another local account can read it —
    /// the same care <see cref="BearerToken"/> already gets on each provider's own config route.
    /// <para>
    /// <see cref="BearerToken"/> wins over an <c>Authorization</c> entry here, so a server that has both configured
    /// sends the credential the auth setting named rather than a stale one typed by hand.
    /// </para>
    /// </summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// Overrides the record's auto-generated <c>ToString()</c>, which would otherwise print <see cref="BearerToken"/>
    /// in the clear — anywhere this lands in a log line or exception message (mirrors <c>CliAgentConfig.ToString()</c>).
    /// </summary>
    public override string ToString() =>
        $"{nameof(PluginMcpServer)} {{ Name = {Name}, Url = {Url}, Command = {Command}, " +
        $"BearerToken = {(string.IsNullOrEmpty(BearerToken) ? "null" : "***")}, " +
        $"Headers = [{string.Join(", ", Headers.Keys)}] }}";
}
