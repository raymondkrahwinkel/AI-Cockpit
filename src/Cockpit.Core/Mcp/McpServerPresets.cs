namespace Cockpit.Core.Mcp;

/// <summary>
/// A one-click template for a well-known MCP server (#26). Local models have no built-in tools, so these
/// presets are the fast path to giving them file access and a few other common capabilities: the user picks
/// a preset, adjusts anything specific (e.g. the folder a filesystem server is scoped to), and saves it into
/// the shared registry. Every resulting tool call is still gated by the approval prompt, and the filesystem
/// preset defaults to a single folder rather than the whole disk — access stays consent-scoped by design.
/// </summary>
public sealed record McpServerPreset(string Label, string Description, McpServerConfig Template);

/// <summary>The built-in preset catalogue offered in the MCP-servers dialog's quick-add row.</summary>
public static class McpServerPresets
{
    /// <summary>
    /// The npm package behind the built-in filesystem preset. The delegated-tool gate keys its name-based
    /// fallback classification (AC-100) on this package — never on the bare server or tool name — so the
    /// "writes are folder-scoped, so a write is a Write not a Destructive" guarantee it relies on only ever
    /// applies to this specific first-party server, not to any server that happens to reuse a generic tool name.
    /// </summary>
    public const string FilesystemServerPackage = "@modelcontextprotocol/server-filesystem";

    /// <summary>
    /// The presets, in the order shown. The filesystem preset is scoped to the user's profile folder by
    /// default — a sensible starting point the user is expected to narrow to the project they want the model
    /// to see. Runtime prerequisites (Node/<c>npx</c> or Python/<c>uvx</c>) are called out in each
    /// description so a preset that can't launch is a clear, not a silent, miss.
    /// </summary>
    public static IReadOnlyList<McpServerPreset> All { get; } =
    [
        // Filesystem/Fetch/Git duplicate tools Claude Code already has (Read/Write, WebFetch, Bash git), so
        // they default to LocalOnly — the local models that lack them. Memory has no Claude equivalent → All.
        new(
            "Filesystem",
            "Read and write files under one folder (needs Node/npx). Defaults to your user folder — narrow the last argument to the project you want the model to reach.",
            new McpServerConfig
            {
                Name = "filesystem",
                Transport = McpTransport.Stdio,
                Scope = McpServerScope.LocalOnly,
                Command = "npx",
                Args = ["-y", FilesystemServerPackage, DefaultFilesystemRoot()],
            }),
        new(
            "Fetch",
            "Fetch a web page and return it as text/markdown (needs Python/uvx).",
            new McpServerConfig
            {
                Name = "fetch",
                Transport = McpTransport.Stdio,
                Scope = McpServerScope.LocalOnly,
                Command = "uvx",
                Args = ["mcp-server-fetch"],
            }),
        new(
            "Git",
            "Inspect and query a local git repository (needs Python/uvx). Set the repository path in the last argument.",
            new McpServerConfig
            {
                Name = "git",
                Transport = McpTransport.Stdio,
                Scope = McpServerScope.LocalOnly,
                Command = "uvx",
                Args = ["mcp-server-git", "--repository", DefaultFilesystemRoot()],
            }),
        new(
            "Memory",
            "A simple persistent knowledge-graph the model can store and recall notes in (needs Node/npx).",
            new McpServerConfig
            {
                Name = "memory",
                Transport = McpTransport.Stdio,
                Command = "npx",
                Args = ["-y", "@modelcontextprotocol/server-memory"],
            }),
    ];

    /// <summary>
    /// The built-in servers every local-model session gets automatically (#26). Local models have no tools of
    /// their own, so these ship on by default; a registry entry with the same name overrides the built-in
    /// (e.g. to point filesystem at a different folder), and disabling it there removes it.
    /// </summary>
    public static IReadOnlyList<McpServerConfig> LocalDefaults { get; } = [.. All.Select(preset => preset.Template)];

    private static string DefaultFilesystemRoot() =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
}
