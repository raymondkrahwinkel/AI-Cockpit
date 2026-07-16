namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>Session-level metadata reported once at the start of a plugin-driven session's stream.</summary>
public sealed record PluginSessionInitialized : PluginSessionEvent
{
    public required IReadOnlyList<string> Tools { get; init; }

    /// <summary>
    /// The working directory the session runs in (#45 D3), so host features that follow the active session's
    /// directory (the git-status header, the active-cwd observer) work for a plugin session too. Optional — a
    /// provider with no directory of its own (an HTTP model) leaves it <see langword="null"/>, and an
    /// already-compiled plugin that never sets it keeps constructing this the old way.
    /// </summary>
    public string? Cwd { get; init; }
}
