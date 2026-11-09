namespace Cockpit.Plugins.Abstractions.Mcp;

/// <summary>How serious an advisory is; <see cref="Ok"/> shows nothing.</summary>
public enum PluginMcpAdvisoryLevel
{
    /// <summary>Nothing to say; the host shows no advisory.</summary>
    Ok,

    /// <summary>Worth knowing before a session starts.</summary>
    Warning,

    /// <summary>Likely to hurt the session.</summary>
    Critical,
}

/// <summary>
/// A short line a plugin has for one of its own MCP servers (AC-2219), shown by the host where that server is chosen
/// or listed: the new-session checklist, Options › MCP Servers and a conversation's details. Never a banner.
/// </summary>
/// <param name="Level">How serious it is.</param>
/// <param name="Text">One plain-language line.</param>
/// <param name="Link">An optional <c>http(s)</c> address the operator can open for more.</param>
public sealed record PluginMcpAdvisory(PluginMcpAdvisoryLevel Level, string Text, string? Link = null);
